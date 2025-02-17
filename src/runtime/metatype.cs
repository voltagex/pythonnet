using System;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;

namespace Python.Runtime
{
    /// <summary>
    /// The managed metatype. This object implements the type of all reflected
    /// types. It also provides support for single-inheritance from reflected
    /// managed types.
    /// </summary>
    internal class MetaType : ManagedType
    {
        private static IntPtr PyCLRMetaType;
        private static SlotsHolder _metaSlotsHodler;

        internal static readonly string[] CustomMethods = new string[]
        {
            "__instancecheck__",
            "__subclasscheck__",
        };

        /// <summary>
        /// Metatype initialization. This bootstraps the CLR metatype to life.
        /// </summary>
        public static IntPtr Initialize()
        {
            PyCLRMetaType = TypeManager.CreateMetaType(typeof(MetaType), out _metaSlotsHodler);
            return PyCLRMetaType;
        }

        public static void Release()
        {
            if (Runtime.Refcount(PyCLRMetaType) > 1)
            {
                _metaSlotsHodler.ResetSlots();
            }
            Runtime.Py_CLEAR(ref PyCLRMetaType);
            _metaSlotsHodler = null;
        }

        internal static void SaveRuntimeData(RuntimeDataStorage storage)
        {
            Runtime.XIncref(PyCLRMetaType);
            storage.PushValue(PyCLRMetaType);
        }

        internal static IntPtr RestoreRuntimeData(RuntimeDataStorage storage)
        {
            PyCLRMetaType = storage.PopValue<IntPtr>();
            _metaSlotsHodler = new SlotsHolder(PyCLRMetaType);
            TypeManager.InitializeSlots(PyCLRMetaType, typeof(MetaType), _metaSlotsHodler);

            IntPtr mdef = Marshal.ReadIntPtr(PyCLRMetaType, TypeOffset.tp_methods);
            foreach (var methodName in CustomMethods)
            {
                var mi = typeof(MetaType).GetMethod(methodName);
                ThunkInfo thunkInfo = Interop.GetThunk(mi, "BinaryFunc");
                _metaSlotsHodler.KeeapAlive(thunkInfo);
                mdef = TypeManager.WriteMethodDef(mdef, methodName, thunkInfo.Address);
            }
            return PyCLRMetaType;
        }

        /// <summary>
        /// Metatype __new__ implementation. This is called to create a new
        /// class / type when a reflected class is subclassed.
        /// </summary>
        public static IntPtr tp_new(IntPtr tp, IntPtr args, IntPtr kw)
        {
            var len = Runtime.PyTuple_Size(args);
            if (len < 3)
            {
                return Exceptions.RaiseTypeError("invalid argument list");
            }

            IntPtr name = Runtime.PyTuple_GetItem(args, 0);
            IntPtr bases = Runtime.PyTuple_GetItem(args, 1);
            IntPtr dict = Runtime.PyTuple_GetItem(args, 2);

            // We do not support multiple inheritance, so the bases argument
            // should be a 1-item tuple containing the type we are subtyping.
            // That type must itself have a managed implementation. We check
            // that by making sure its metatype is the CLR metatype.

            if (Runtime.PyTuple_Size(bases) != 1)
            {
                return Exceptions.RaiseTypeError("cannot use multiple inheritance with managed classes");
            }

            IntPtr base_type = Runtime.PyTuple_GetItem(bases, 0);
            IntPtr mt = Runtime.PyObject_TYPE(base_type);

            if (!(mt == PyCLRMetaType || mt == Runtime.PyTypeType))
            {
                return Exceptions.RaiseTypeError("invalid metatype");
            }

            // Ensure that the reflected type is appropriate for subclassing,
            // disallowing subclassing of delegates, enums and array types.

            var cb = GetManagedObject(base_type) as ClassBase;
            if (cb != null)
            {
                try
                {
                    if (!cb.CanSubclass())
                    {
                        return Exceptions.RaiseTypeError("delegates, enums and array types cannot be subclassed");
                    }
                }
                catch (SerializationException)
                {
                    return Exceptions.RaiseTypeError($"Underlying C# Base class {cb.type} has been deleted");
                }
            }

            IntPtr slots = Runtime.PyDict_GetItem(dict, PyIdentifier.__slots__);
            if (slots != IntPtr.Zero)
            {
                return Exceptions.RaiseTypeError("subclasses of managed classes do not support __slots__");
            }

            // If __assembly__ or __namespace__ are in the class dictionary then create
            // a managed sub type.
            // This creates a new managed type that can be used from .net to call back
            // into python.
            if (IntPtr.Zero != dict)
            {
                using (var clsDict = new PyDict(new BorrowedReference(dict)))
                {
                    if (clsDict.HasKey("__assembly__") || clsDict.HasKey("__namespace__"))
                    {
                        return TypeManager.CreateSubType(name, base_type, clsDict.Reference);
                    }
                }
            }

            // otherwise just create a basic type without reflecting back into the managed side.
            IntPtr func = Marshal.ReadIntPtr(Runtime.PyTypeType, TypeOffset.tp_new);
            IntPtr type = NativeCall.Call_3(func, tp, args, kw);
            if (type == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var flags = (TypeFlags)Util.ReadCLong(type, TypeOffset.tp_flags);
            if (!flags.HasFlag(TypeFlags.Ready))
                throw new NotSupportedException("PyType.tp_new returned an incomplete type");
            flags |= TypeFlags.HasClrInstance;
            flags |= TypeFlags.HeapType;
            flags |= TypeFlags.BaseType;
            flags |= TypeFlags.Subclass;
            flags |= TypeFlags.HaveGC;
            Util.WriteCLong(type, TypeOffset.tp_flags, (int)flags);

            TypeManager.CopySlot(base_type, type, TypeOffset.tp_dealloc);

            // Hmm - the standard subtype_traverse, clear look at ob_size to
            // do things, so to allow gc to work correctly we need to move
            // our hidden handle out of ob_size. Then, in theory we can
            // comment this out and still not crash.
            TypeManager.CopySlot(base_type, type, TypeOffset.tp_traverse);
            TypeManager.CopySlot(base_type, type, TypeOffset.tp_clear);

            // derived types must have their GCHandle at the same offset as the base types
            int clrInstOffset = Marshal.ReadInt32(base_type, Offsets.tp_clr_inst_offset);
            Marshal.WriteInt32(type, Offsets.tp_clr_inst_offset, clrInstOffset);

            // for now, move up hidden handle...
            IntPtr gc = Marshal.ReadIntPtr(base_type, Offsets.tp_clr_inst);
            Marshal.WriteIntPtr(type, Offsets.tp_clr_inst, gc);

            Runtime.PyType_Modified(new BorrowedReference(type));

            return type;
        }


        public static IntPtr tp_alloc(IntPtr mt, int n)
        {
            IntPtr type = Runtime.PyType_GenericAlloc(mt, n);
            return type;
        }


        public static void tp_free(IntPtr tp)
        {
            Runtime.PyObject_GC_Del(tp);
        }


        /// <summary>
        /// Metatype __call__ implementation. This is needed to ensure correct
        /// initialization (__init__ support), because the tp_call we inherit
        /// from PyType_Type won't call __init__ for metatypes it doesn't know.
        /// </summary>
        public static IntPtr tp_call(IntPtr tp, IntPtr args, IntPtr kw)
        {
            IntPtr func = Marshal.ReadIntPtr(tp, TypeOffset.tp_new);
            if (func == IntPtr.Zero)
            {
                return Exceptions.RaiseTypeError("invalid object");
            }

            IntPtr obj = NativeCall.Call_3(func, tp, args, kw);
            if (obj == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            return CallInit(obj, args, kw);
        }

        private static IntPtr CallInit(IntPtr obj, IntPtr args, IntPtr kw)
        {
            var init = Runtime.PyObject_GetAttr(obj, PyIdentifier.__init__);
            Runtime.PyErr_Clear();

            if (init != IntPtr.Zero)
            {
                IntPtr result = Runtime.PyObject_Call(init, args, kw);
                Runtime.XDecref(init);

                if (result == IntPtr.Zero)
                {
                    Runtime.XDecref(obj);
                    return IntPtr.Zero;
                }

                Runtime.XDecref(result);
            }

            return obj;
        }


        /// <summary>
        /// Type __setattr__ implementation for reflected types. Note that this
        /// is slightly different than the standard setattr implementation for
        /// the normal Python metatype (PyTypeType). We need to look first in
        /// the type object of a reflected type for a descriptor in order to
        /// support the right setattr behavior for static fields and properties.
        /// </summary>
        public static int tp_setattro(IntPtr tp, IntPtr name, IntPtr value)
        {
            IntPtr descr = Runtime._PyType_Lookup(tp, name);

            if (descr != IntPtr.Zero)
            {
                IntPtr dt = Runtime.PyObject_TYPE(descr);

                if (dt == Runtime.PyWrapperDescriptorType
                    || dt == Runtime.PyMethodType
                    || typeof(ExtensionType).IsInstanceOfType(GetManagedObject(descr))
                )
                {
                    IntPtr fp = Marshal.ReadIntPtr(dt, TypeOffset.tp_descr_set);
                    if (fp != IntPtr.Zero)
                    {
                        return NativeCall.Int_Call_3(fp, descr, name, value);
                    }
                    Exceptions.SetError(Exceptions.AttributeError, "attribute is read-only");
                    return -1;
                }
            }

            int res = Runtime.PyObject_GenericSetAttr(tp, name, value);
            Runtime.PyType_Modified(new BorrowedReference(tp));

            return res;
        }

        /// <summary>
        /// The metatype has to implement [] semantics for generic types, so
        /// here we just delegate to the generic type def implementation. Its
        /// own mp_subscript
        /// </summary>
        public static IntPtr mp_subscript(IntPtr tp, IntPtr idx)
        {
            var cb = GetManagedObject(tp) as ClassBase;
            if (cb != null)
            {
                return cb.type_subscript(idx);
            }
            return Exceptions.RaiseTypeError("unsubscriptable object");
        }

        /// <summary>
        /// Dealloc implementation. This is called when a Python type generated
        /// by this metatype is no longer referenced from the Python runtime.
        /// </summary>
        public static void tp_dealloc(IntPtr tp)
        {
            // Fix this when we dont cheat on the handle for subclasses!

            var flags = (TypeFlags)Util.ReadCLong(tp, TypeOffset.tp_flags);
            if ((flags & TypeFlags.Subclass) == 0)
            {
                GetGCHandle(new BorrowedReference(tp)).Free();
#if DEBUG
                // prevent ExecutionEngineException in debug builds in case we have a bug
                // this would allow using managed debugger to investigate the issue
                SetGCHandle(new BorrowedReference(tp), Runtime.CLRMetaType, default);
#endif
            }

            IntPtr op = Marshal.ReadIntPtr(tp, TypeOffset.ob_type);
            Runtime.XDecref(op);

            // Delegate the rest of finalization the Python metatype. Note
            // that the PyType_Type implementation of tp_dealloc will call
            // tp_free on the type of the type being deallocated - in this
            // case our CLR metatype. That is why we implement tp_free.

            op = Marshal.ReadIntPtr(Runtime.PyTypeType, TypeOffset.tp_dealloc);
            NativeCall.Void_Call_1(op, tp);
        }

        private static IntPtr DoInstanceCheck(IntPtr tp, IntPtr args, bool checkType)
        {
            var cb = GetManagedObject(tp) as ClassBase;

            if (cb == null || !cb.type.Valid)
            {
                Runtime.XIncref(Runtime.PyFalse);
                return Runtime.PyFalse;
            }

            using (var argsObj = new PyList(new BorrowedReference(args)))
            {
                if (argsObj.Length() != 1)
                {
                    return Exceptions.RaiseTypeError("Invalid parameter count");
                }

                PyObject arg = argsObj[0];
                PyObject otherType;
                if (checkType)
                {
                    otherType = arg;
                }
                else
                {
                    otherType = arg.GetPythonType();
                }

                if (Runtime.PyObject_TYPE(otherType.Handle) != PyCLRMetaType)
                {
                    Runtime.XIncref(Runtime.PyFalse);
                    return Runtime.PyFalse;
                }

                var otherCb = GetManagedObject(otherType.Handle) as ClassBase;
                if (otherCb == null || !otherCb.type.Valid)
                {
                    Runtime.XIncref(Runtime.PyFalse);
                    return Runtime.PyFalse;
                }

                return Converter.ToPython(cb.type.Value.IsAssignableFrom(otherCb.type.Value));
            }
        }

        public static IntPtr __instancecheck__(IntPtr tp, IntPtr args)
        {
            return DoInstanceCheck(tp, args, false);
        }

        public static IntPtr __subclasscheck__(IntPtr tp, IntPtr args)
        {
            return DoInstanceCheck(tp, args, true);
        }
    }
}
