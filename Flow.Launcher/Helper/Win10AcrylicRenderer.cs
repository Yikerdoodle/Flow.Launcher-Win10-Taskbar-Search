using System;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;

namespace Flow.Launcher.Helper
{
    /// <summary>
    /// Software rendition of the acrylic of the Windows 10 Start menu and search panel. The system's own pipeline, as measured
    /// from the native panels (light mode, Transparency effects on): the backdrop under the panel is blurred (Gaussian, 30 DIP
    /// sigma, mirrored at the panel's edges), saturated, luminosity-blended with the tint (W3C SetLum/ClipColor), mixed with
    /// the tint colour, and a fixed 256x256 integer noise texture is added. Operates on plain pixel arrays (BGRA).
    /// </summary>
    internal static class Win10AcrylicRenderer
    {
        /// <summary>Colour response of one panel.</summary>
        internal readonly record struct Look(double Saturation, double LuminosityOpacity, double TintOpacity, double TintR, double TintG, double TintB, double LuminosityTarget);

        /// <summary>Where the blur's mirror edges are, in pixels of the panel at 150% scale, inside the panel's visible rectangle.</summary>
        internal readonly record struct Insets(double Left, double Top, double Right, double Bottom);

        internal static readonly Look StartLight = new(0.540054371704427, 0.9008082467641607, 0.03245196648329378, -0.7210679548759318, -0.7207957042870184, -0.7214763313364486, 0.9429124184107964);
        internal static readonly Look SearchLight = new(0.6626286082481148, 0.8079161972434671, 0.01913878467029378, -0.11188258127078876, -0.11059795616270429, -0.11133830895881038, 0.9766497073793261);

        internal static readonly Insets StartInsets = new(5, 3, 9, 5);
        internal static readonly Insets SearchInsets = new(5, 3.5, 4.5, 3);

        // Blur sigma in DIPs; the noise texture's offset of the panel's own origin (measured: the same for both panels)
        private const double SigmaDip = 30;
        private const int NoiseOffsetY = 60;
        // Mean of the noise texture; the colour response was measured with it included
        private const double NoiseMean = 0.5154;
        // Tail of the Gaussian cut off beyond this many sigmas
        private const double KernelSigmas = 4;

        private static sbyte[]? _noise;

        private static sbyte[] Noise()
        {
            if (_noise != null) return _noise;
            var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Resources", "win10-noise.bin"));
            var n = new sbyte[bytes.Length];
            for (var i = 0; i < n.Length; i++) n[i] = (sbyte)(bytes[i] - 2);
            return _noise = n;
        }

        /// <summary>
        /// Renders the acrylic for a panel of w x h pixels from the backdrop under it (BGRA, stride w*4). The result is
        /// BGRA, opaque. scale is the DPI scale (1.5 for 144 dpi).
        /// </summary>
        internal static byte[] Render(byte[] backdropBgra, int w, int h, Look look, Insets insets, double scale, int downscale = 2)
        {
            var inset = scale / 1.5;
            var x0 = Math.Clamp((int)Math.Round(insets.Left * inset), 0, Math.Max(0, w / 2 - 1));
            var y0 = Math.Clamp((int)Math.Round(insets.Top * inset), 0, Math.Max(0, h / 2 - 1));
            var x1 = Math.Clamp(w - (int)Math.Round(insets.Right * inset), x0 + 1, w);
            var y1 = Math.Clamp(h - (int)Math.Round(insets.Bottom * inset), y0 + 1, h);
            var dw = x1 - x0;
            var dh = y1 - y0;

            // Planar float channels of the blur domain
            var planes = new float[3][];
            for (var c = 0; c < 3; c++) planes[c] = new float[dw * dh];
            Parallel.For(0, dh, y =>
            {
                var src = (y0 + y) * w * 4 + x0 * 4;
                var dst = y * dw;
                for (var x = 0; x < dw; x++, src += 4, dst++)
                {
                    planes[0][dst] = backdropBgra[src + 2];
                    planes[1][dst] = backdropBgra[src + 1];
                    planes[2][dst] = backdropBgra[src];
                }
            });

            // The blur is so wide that it can be done on a reduced copy (2x: indistinguishable from the full resolution one
            // at the output's precision, 4x is not) which is much faster
            var f = downscale > 1 && Math.Min(dw, dh) >= 8 * downscale ? downscale : 1;
            var sigma = SigmaDip * scale;
            if (f == 1)
            {
                var kernel = GaussianKernel(sigma);
                Parallel.For(0, 3, c => planes[c] = Blur(planes[c], dw, dh, kernel));
            }
            else
            {
                var sw = dw / f;
                var sh = dh / f;
                var kernel = GaussianKernel(sigma / f);
                Parallel.For(0, 3, c =>
                {
                    var small = BoxDown(planes[c], dw, f, sw, sh);
                    small = Blur(small, sw, sh, kernel);
                    planes[c] = BilinearUp(small, sw, sh, f, dw, dh);
                });
            }

            var noise = Noise();
            var output = new byte[w * h * 4];
            var tintR = look.TintR;
            var tintG = look.TintG;
            var tintB = look.TintB;
            Parallel.For(0, h, y =>
            {
                var sy = Math.Clamp(y - y0, 0, dh - 1);
                var noiseRow = ((y + NoiseOffsetY) & 255) * 256;
                for (var x = 0; x < w; x++)
                {
                    var sx = Math.Clamp(x - x0, 0, dw - 1);
                    var i = sy * dw + sx;
                    Colour(planes[0][i] / 255.0, planes[1][i] / 255.0, planes[2][i] / 255.0, look, out var fr, out var fg, out var fb);
                    // Rounded to nearest: floor(v + 0.5), with the 0.5 offset of the measured response folded in
                    var n = noise[noiseRow + (x & 255)] - NoiseMean + 1.0;
                    var o = (y * w + x) * 4;
                    output[o] = ToByte(fb + n);
                    output[o + 1] = ToByte(fg + n);
                    output[o + 2] = ToByte(fr + n);
                    output[o + 3] = 255;
                }
            });
            return output;
        }

        private static byte ToByte(double v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)Math.Floor(v);

        // Luminosity of a colour; W3C compositing weights
        private const double Wr = 0.3, Wg = 0.59, Wb = 0.11;

        private static void Colour(double r, double g, double b, in Look look, out double outR, out double outG, out double outB)
        {
            // Saturation
            var l = Wr * r + Wg * g + Wb * b;
            var s = look.Saturation;
            var sr = l + s * (r - l);
            var sg = l + s * (g - l);
            var sb = l + s * (b - l);

            // Luminosity blend of the tint's luminosity (SetLum, ClipColor), mixed with the saturated backdrop
            var ls = Wr * sr + Wg * sg + Wb * sb;
            var d = look.LuminosityTarget - ls;
            var br = sr + d;
            var bg = sg + d;
            var bb = sb + d;
            var bl = Wr * br + Wg * bg + Wb * bb;
            var n = Math.Min(br, Math.Min(bg, bb));
            var x = Math.Max(br, Math.Max(bg, bb));
            if (n < 0)
            {
                var k = bl / Math.Max(bl - n, 1e-9);
                br = bl + (br - bl) * k;
                bg = bl + (bg - bl) * k;
                bb = bl + (bb - bl) * k;
            }
            if (x > 1)
            {
                x = Math.Max(br, Math.Max(bg, bb));
                var k = (1 - bl) / Math.Max(x - bl, 1e-9);
                br = bl + (br - bl) * k;
                bg = bl + (bg - bl) * k;
                bb = bl + (bb - bl) * k;
            }
            var a = look.LuminosityOpacity;
            var c1r = (1 - a) * sr + a * br;
            var c1g = (1 - a) * sg + a * bg;
            var c1b = (1 - a) * sb + a * bb;

            // Tint
            var t = look.TintOpacity;
            outR = 255 * ((1 - t) * c1r + t * look.TintR);
            outG = 255 * ((1 - t) * c1g + t * look.TintG);
            outB = 255 * ((1 - t) * c1b + t * look.TintB);
        }

        private static float[] GaussianKernel(double sigma)
        {
            var radius = Math.Max(1, (int)Math.Ceiling(KernelSigmas * sigma));
            var k = new float[radius * 2 + 1];
            double sum = 0;
            for (var i = -radius; i <= radius; i++)
            {
                var v = Math.Exp(-i * (double)i / (2 * sigma * sigma));
                k[i + radius] = (float)v;
                sum += v;
            }
            for (var i = 0; i < k.Length; i++) k[i] = (float)(k[i] / sum);
            return k;
        }

        // Average of f x f blocks; the remainder rows and columns, if any, are left out
        private static float[] BoxDown(float[] src, int w, int f, int sw, int sh)
        {
            var dst = new float[sw * sh];
            var norm = 1f / (f * f);
            for (var y = 0; y < sh; y++)
                for (var x = 0; x < sw; x++)
                {
                    float acc = 0;
                    for (var j = 0; j < f; j++)
                    {
                        var row = (y * f + j) * w + x * f;
                        for (var i = 0; i < f; i++) acc += src[row + i];
                    }
                    dst[y * sw + x] = acc * norm;
                }
            return dst;
        }

        private static float[] BilinearUp(float[] src, int sw, int sh, int f, int w, int h)
        {
            var dst = new float[w * h];
            var xi0 = new int[w];
            var xi1 = new int[w];
            var xf = new float[w];
            for (var x = 0; x < w; x++)
            {
                var xs = (x + 0.5) / f - 0.5;
                var fl = Math.Floor(xs);
                xi0[x] = Math.Clamp((int)fl, 0, sw - 1);
                xi1[x] = Math.Clamp((int)fl + 1, 0, sw - 1);
                xf[x] = (float)(xs - fl);
            }
            Parallel.For(0, h, y =>
            {
                var ys = (y + 0.5) / f - 0.5;
                var fl = Math.Floor(ys);
                var r0 = Math.Clamp((int)fl, 0, sh - 1) * sw;
                var r1 = Math.Clamp((int)fl + 1, 0, sh - 1) * sw;
                var fy = (float)(ys - fl);
                var o = y * w;
                for (var x = 0; x < w; x++)
                {
                    var top = src[r0 + xi0[x]] * (1 - xf[x]) + src[r0 + xi1[x]] * xf[x];
                    var bottom = src[r1 + xi0[x]] * (1 - xf[x]) + src[r1 + xi1[x]] * xf[x];
                    dst[o + x] = top * (1 - fy) + bottom * fy;
                }
            });
            return dst;
        }

        // Mirror index (edge pixel not repeated), valid for any distance beyond the edge
        private static int Mirror(int i, int n)
        {
            if (n == 1) return 0;
            var period = 2 * (n - 1);
            i %= period;
            if (i < 0) i += period;
            return i < n ? i : period - i;
        }

        private static float[] Blur(float[] src, int w, int h, float[] kernel)
        {
            var r = kernel.Length / 2;
            var tmp = new float[w * h];

            // Horizontal: per row a mirrored, padded copy; Vector<float> over the output pixels
            Parallel.For(0, h, y =>
            {
                var padded = new float[w + 2 * r + Vector<float>.Count];
                var row = y * w;
                for (var x = 0; x < w + 2 * r; x++) padded[x] = src[row + Mirror(x - r, w)];
                var vc = Vector<float>.Count;
                var x0 = 0;
                for (; x0 + vc <= w; x0 += vc)
                {
                    var acc = Vector<float>.Zero;
                    for (var j = 0; j < kernel.Length; j++) acc += kernel[j] * new Vector<float>(padded, x0 + j);
                    acc.CopyTo(tmp, row + x0);
                }
                for (; x0 < w; x0++)
                {
                    float acc = 0;
                    for (var j = 0; j < kernel.Length; j++) acc += kernel[j] * padded[x0 + j];
                    tmp[row + x0] = acc;
                }
            });

            // Vertical: each output row is the weighted sum of mirrored input rows
            var dst = new float[w * h];
            Parallel.For(0, h, y =>
            {
                var vc = Vector<float>.Count;
                var rowOut = y * w;
                var x0 = 0;
                for (; x0 + vc <= w; x0 += vc)
                {
                    var acc = Vector<float>.Zero;
                    for (var j = 0; j < kernel.Length; j++)
                    {
                        var rowIn = Mirror(y + j - r, h) * w;
                        acc += kernel[j] * new Vector<float>(tmp, rowIn + x0);
                    }
                    acc.CopyTo(dst, rowOut + x0);
                }
                for (; x0 < w; x0++)
                {
                    float acc = 0;
                    for (var j = 0; j < kernel.Length; j++) acc += kernel[j] * tmp[Mirror(y + j - r, h) * w + x0];
                    dst[rowOut + x0] = acc;
                }
            });
            return dst;
        }
    }
}
