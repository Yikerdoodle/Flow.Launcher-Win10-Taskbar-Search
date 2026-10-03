using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Graphics.Effects;
using WinRT;

namespace Flow.Launcher.Helper;

/// <summary>
/// Effect descriptions for Windows.UI.Composition's effect graphs, without Win2D. The compositor asks an effect description
/// for the Direct2D effect it stands for (its CLSID), that effect's properties and its sources, through
/// <c>IGraphicsEffect</c> and <c>IGraphicsEffectD2D1Interop</c>; .NET has no type for those, so a node here is a small native
/// COM object, built by hand, that answers those questions from a managed description.
/// </summary>
internal static unsafe class CompositionEffects
{
    internal static readonly Guid GaussianBlur = new("1FEB6D69-2FE6-4AC9-8C58-1D7F93E7A6A5");
    internal static readonly Guid ColorMatrix = new("921F03D6-641C-47DF-852D-B4BB6153AE11");
    internal static readonly Guid Blend = new("81C5B77B-13F8-4CDD-AD20-C890547AC65D");
    internal static readonly Guid ArithmeticComposite = new("FC151437-049A-4784-A24A-F1C4DAF20987");
    internal static readonly Guid Flood = new("61C23C20-AE69-4D8E-94CF-50078DF638F2");
    internal static readonly Guid Composite = new("48FC9F51-F6AC-48F1-8B58-3B28AC46F76D");
    internal static readonly Guid Border = new("2A2D49C0-4ACF-43C7-8C6A-7C4A27874D27");

    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidInspectable = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");
    private static readonly Guid IidEffect = new("CB51C0CE-8FE6-4636-B202-861FAA07D8F3");
    private static readonly Guid IidSource = new("2D8F9DDC-4339-4EB9-9216-F9DEB75658A2");
    private static readonly Guid IidInterop = new("2FC57384-A068-44D7-A331-30982FCF7177");
    private static readonly Guid IidPropertyValueStatics = new("629BDBC8-D932-4FF4-96B9-8D96C5C1E858");

    [DllImport("combase.dll")]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern char* WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr classId, in Guid iid, out IntPtr factory);

    // A node is one native allocation: a vtable pointer per interface it has (IGraphicsEffect, IGraphicsEffectSource,
    // IGraphicsEffectD2D1Interop), a reference count, and a handle of its managed description
    private const int Slot0 = 0, Slot1 = 8, Slot2 = 16, RefOffset = 24, HandleOffset = 32, NodeSize = 40;

    private static readonly IntPtr Vtable0, Vtable1, Vtable2;

    static CompositionEffects()
    {
        // IUnknown, IInspectable, IGraphicsEffect (Name)
        var v0 = (IntPtr*)NativeMemory.Alloc(8, (nuint)sizeof(IntPtr));
        v0[0] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&QueryInterface0;
        v0[1] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRef;
        v0[2] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&Release;
        v0[3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint*, Guid**, int>)&GetIids;
        v0[4] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)&GetRuntimeClassName;
        v0[5] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int*, int>)&GetTrustLevel;
        v0[6] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)&GetName;
        v0[7] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&PutName;
        Vtable0 = (IntPtr)v0;

        // IUnknown, IInspectable (IGraphicsEffectSource has no methods of its own)
        var v1 = (IntPtr*)NativeMemory.Alloc(6, (nuint)sizeof(IntPtr));
        v1[0] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&QueryInterface1;
        v1[1] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRef;
        v1[2] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&Release;
        v1[3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint*, Guid**, int>)&GetIids;
        v1[4] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)&GetRuntimeClassName;
        v1[5] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int*, int>)&GetTrustLevel;
        Vtable1 = (IntPtr)v1;

        // IUnknown, IGraphicsEffectD2D1Interop
        var v2 = (IntPtr*)NativeMemory.Alloc(9, (nuint)sizeof(IntPtr));
        v2[0] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&QueryInterface2;
        v2[1] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRef;
        v2[2] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&Release;
        v2[3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, int>)&GetEffectId;
        v2[4] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, char*, uint*, int*, int>)&GetNamedPropertyMapping;
        v2[5] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)&GetPropertyCount;
        v2[6] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)&GetProperty;
        v2[7] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)&GetSource;
        v2[8] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)&GetSourceCount;
        Vtable2 = (IntPtr)v2;
    }

    /// <summary>One Direct2D effect of an effect graph.</summary>
    internal sealed class Node
    {
        internal readonly IntPtr Pointer;
        internal readonly Guid EffectId;
        internal string Name = "";

        // float, float[] (vectors and matrices), uint or bool, in the effect's property order
        internal readonly List<object> Properties = new();

        // Nodes, or any projected IGraphicsEffectSource such as a CompositionEffectSourceParameter. In a graph every
        // source parameter may be used only once (the compositor refuses it otherwise): use one parameter per use.
        internal readonly List<object> Sources = new();

        internal Node(Guid effectId, object[] properties, params object[] sources)
        {
            EffectId = effectId;
            Properties.AddRange(properties);
            Sources.AddRange(sources);
            Pointer = (IntPtr)NativeMemory.Alloc(NodeSize);
            *(IntPtr*)((byte*)Pointer + Slot0) = Vtable0;
            *(IntPtr*)((byte*)Pointer + Slot1) = Vtable1;
            *(IntPtr*)((byte*)Pointer + Slot2) = Vtable2;
            *(int*)((byte*)Pointer + RefOffset) = 1;
            // Nodes live as long as the process: a few small objects, and the compositor holds on to them
            *(IntPtr*)((byte*)Pointer + HandleOffset) = GCHandle.ToIntPtr(GCHandle.Alloc(this));
        }

        /// <summary>The node as the effect description the compositor takes.</summary>
        internal IGraphicsEffect AsEffect()
        {
            Marshal.AddRef(Pointer);
            return MarshalInterface<IGraphicsEffect>.FromAbi(Pointer);
        }
    }

    private static Node NodeOf(IntPtr self, int slot) =>
        (Node)GCHandle.FromIntPtr(*(IntPtr*)((byte*)(self - slot) + HandleOffset)).Target!;

    private static int QueryInterface(IntPtr self, int slot, Guid* iid, IntPtr* result)
    {
        var node = self - slot;
        int target;
        if (*iid == IidUnknown || *iid == IidInspectable || *iid == IidEffect) target = Slot0;
        else if (*iid == IidSource) target = Slot1;
        else if (*iid == IidInterop) target = Slot2;
        else
        {
            *result = IntPtr.Zero;
            return unchecked((int)0x80004002); // E_NOINTERFACE
        }

        Interlocked.Increment(ref *(int*)((byte*)node + RefOffset));
        *result = node + target;
        return 0;
    }

    // The three interfaces share one count; the slot of the pointer is not needed to find it
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint AddRef(IntPtr self) => (uint)Interlocked.Increment(ref *(int*)((byte*)(self - SlotOf(self)) + RefOffset));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint Release(IntPtr self) => (uint)Interlocked.Decrement(ref *(int*)((byte*)(self - SlotOf(self)) + RefOffset));

    // Which of its slots an interface pointer is: the vtable it points at says
    private static int SlotOf(IntPtr self)
    {
        var vtable = *(IntPtr*)self;
        return vtable == Vtable0 ? Slot0 : vtable == Vtable1 ? Slot1 : Slot2;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int QueryInterface0(IntPtr self, Guid* iid, IntPtr* result) => QueryInterface(self, Slot0, iid, result);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int QueryInterface1(IntPtr self, Guid* iid, IntPtr* result) => QueryInterface(self, Slot1, iid, result);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int QueryInterface2(IntPtr self, Guid* iid, IntPtr* result) => QueryInterface(self, Slot2, iid, result);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetIids(IntPtr self, uint* count, Guid** iids)
    {
        *count = 0;
        *iids = null;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetRuntimeClassName(IntPtr self, IntPtr* name)
    {
        const string className = "Flow.Launcher.GraphicsEffect";
        return WindowsCreateString(className, className.Length, out *name);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetTrustLevel(IntPtr self, int* level)
    {
        *level = 0;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetName(IntPtr self, IntPtr* name)
    {
        var text = NodeOf(self, Slot0).Name;
        return WindowsCreateString(text, text.Length, out *name);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int PutName(IntPtr self, IntPtr name)
    {
        var buffer = WindowsGetStringRawBuffer(name, out var length);
        NodeOf(self, Slot0).Name = new string(buffer, 0, (int)length);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetEffectId(IntPtr self, Guid* id)
    {
        *id = NodeOf(self, Slot2).EffectId;
        return 0;
    }

    // Only for animating named properties, which these effects do not use
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetNamedPropertyMapping(IntPtr self, char* name, uint* index, int* mapping) => unchecked((int)0x80070057); // E_INVALIDARG

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetPropertyCount(IntPtr self, uint* count)
    {
        *count = (uint)NodeOf(self, Slot2).Properties.Count;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetProperty(IntPtr self, uint index, IntPtr* value)
    {
        try
        {
            *value = NodeOf(self, Slot2).Properties[(int)index] switch
            {
                float f => PropertySingle(f),
                float[] a => PropertySingleArray(a),
                uint u => PropertyUInt32(u),
                bool b => PropertyBoolean(b),
                _ => throw new InvalidOperationException("Unsupported effect property type")
            };
            return 0;
        }
        catch (Exception e)
        {
            return e.HResult;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetSource(IntPtr self, uint index, IntPtr* source)
    {
        try
        {
            var item = NodeOf(self, Slot2).Sources[(int)index];
            if (item is Node node)
            {
                Marshal.AddRef(node.Pointer);
                *source = node.Pointer + Slot1;
            }
            else
            {
                *source = MarshalInterface<IGraphicsEffectSource>.FromManaged((IGraphicsEffectSource)item);
            }
            return 0;
        }
        catch (Exception e)
        {
            return e.HResult;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetSourceCount(IntPtr self, uint* count)
    {
        *count = (uint)NodeOf(self, Slot2).Sources.Count;
        return 0;
    }

    // Real IPropertyValue objects, from the platform's PropertyValue factory (the projection would hand out plain .NET values)
    private static IntPtr _propertyValues;

    private static IntPtr PropertyValues()
    {
        if (_propertyValues != IntPtr.Zero) return _propertyValues;
        const string className = "Windows.Foundation.PropertyValue";
        WindowsCreateString(className, className.Length, out var name);
        var hr = RoGetActivationFactory(name, in IidPropertyValueStatics, out var factory);
        WindowsDeleteString(name);
        if (hr != 0) throw new InvalidOperationException("PropertyValue factory 0x" + hr.ToString("X"));
        return _propertyValues = factory;
    }

    // Methods of IPropertyValueStatics: after IInspectable (6 entries) CreateEmpty, CreateUInt8, ...
    private static IntPtr Method(int index) => ((IntPtr*)*(IntPtr*)PropertyValues())[index];

    private static IntPtr PropertySingle(float value)
    {
        IntPtr result;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, float, IntPtr*, int>)Method(14))(PropertyValues(), value, &result));
        return result;
    }

    private static IntPtr PropertyUInt32(uint value)
    {
        IntPtr result;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Method(11))(PropertyValues(), value, &result));
        return result;
    }

    private static IntPtr PropertyBoolean(bool value)
    {
        IntPtr result;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, byte, IntPtr*, int>)Method(17))(PropertyValues(), value ? (byte)1 : (byte)0, &result));
        return result;
    }

    private static IntPtr PropertySingleArray(float[] value)
    {
        IntPtr result;
        fixed (float* data = value)
        {
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, float*, IntPtr*, int>)Method(33))(PropertyValues(), (uint)value.Length, data, &result));
        }
        return result;
    }

    private static void Check(int hr)
    {
        if (hr != 0) throw new InvalidOperationException("PropertyValue 0x" + hr.ToString("X"));
    }
}
