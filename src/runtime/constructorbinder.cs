using System;
using System.Reflection;
using System.Text;

namespace Python.Runtime
{
    /// <summary>
    /// A ConstructorBinder encapsulates information about one or more managed
    /// constructors, and is responsible for selecting the right constructor
    /// given a set of Python arguments. This is slightly different than the
    /// standard MethodBinder because of a difference in invoking constructors
    /// using reflection (which is seems to be a CLR bug).
    /// </summary>
    [Serializable]
    internal class ConstructorBinder : MethodBinder
    {
        private MaybeType _containingType;

        internal ConstructorBinder(Type containingType)
        {
            _containingType = containingType;
        }

        /// <summary>
        /// Constructors get invoked when an instance of a wrapped managed
        /// class or a subclass of a managed class is created. This differs
        /// from the MethodBinder implementation in that we return the raw
        /// result of the constructor rather than wrapping it as a Python
        /// object - the reason is that only the caller knows the correct
        /// Python type to use when wrapping the result (may be a subclass).
        /// </summary>
        internal object InvokeRaw(IntPtr inst, IntPtr args, IntPtr kw)
        {
            return InvokeRaw(inst, args, kw, null);
        }

        /// <summary>
        /// Allows ctor selection to be limited to a single attempt at a
        /// match by providing the MethodBase to use instead of searching
        /// the entire MethodBinder.list (generic ArrayList)
        /// </summary>
        /// <param name="inst"> (possibly null) instance </param>
        /// <param name="args"> PyObject* to the arg tuple </param>
        /// <param name="kw"> PyObject* to the keyword args dict </param>
        /// <param name="info"> The sole ContructorInfo to use or null </param>
        /// <returns> The result of the constructor call with converted params </returns>
        /// <remarks>
        /// 2010-07-24 BC: I added the info parameter to the call to Bind()
        /// Binding binding = this.Bind(inst, args, kw, info);
        /// to take advantage of Bind()'s ability to use a single MethodBase (CI or MI).
        /// </remarks>
        internal object InvokeRaw(IntPtr inst, IntPtr args, IntPtr kw, MethodBase info)
        {
            if (!_containingType.Valid)
            {
                return Exceptions.RaiseTypeError(_containingType.DeletedMessage);
            }
            object result;
            Type tp = _containingType.Value;

            if (tp.IsValueType && !tp.IsPrimitive &&
                !tp.IsEnum && tp != typeof(decimal) &&
                Runtime.PyTuple_Size(args) == 0)
            {
                // If you are trying to construct an instance of a struct by
                // calling its default constructor, that ConstructorInfo
                // instance will not appear in reflection and the object must
                // instead be constructed via a call to
                // Activator.CreateInstance().
                try
                {
                    result = Activator.CreateInstance(tp);
                }
                catch (Exception e)
                {
                    if (e.InnerException != null)
                    {
                        e = e.InnerException;
                    }
                    Exceptions.SetError(e);
                    return null;
                }
                return result;
            }

            Binding binding = Bind(inst, args, kw, info);

            if (binding == null)
            {
                // It is possible for __new__ to be invoked on construction
                // of a Python subclass of a managed class, so args may
                // reflect more args than are required to instantiate the
                // class. So if we cant find a ctor that matches, we'll see
                // if there is a default constructor and, if so, assume that
                // any extra args are intended for the subclass' __init__.

                IntPtr eargs = Runtime.PyTuple_New(0);
                binding = Bind(inst, eargs, IntPtr.Zero);
                Runtime.XDecref(eargs);

                if (binding == null)
                {
                    var errorMessage = new StringBuilder("No constructor matches given arguments");
                    if (info != null && info.IsConstructor && info.DeclaringType != null)
                    {
                        errorMessage.Append(" for ").Append(info.DeclaringType.Name);
                    }

                    errorMessage.Append(": ");
                    Runtime.PyErr_Fetch(out var errType, out var errVal, out var errTrace);
                    AppendArgumentTypes(to: errorMessage, args);
                    Runtime.PyErr_Restore(errType.StealNullable(), errVal.StealNullable(), errTrace.StealNullable());
                    Exceptions.RaiseTypeError(errorMessage.ToString());
                    return null;
                }
            }

            // Fire the selected ctor and catch errors...
            var ci = (ConstructorInfo)binding.info;
            // Object construction is presumed to be non-blocking and fast
            // enough that we shouldn't really need to release the GIL.
            try
            {
                result = ci.Invoke(binding.args);
            }
            catch (Exception e)
            {
                if (e.InnerException != null)
                {
                    e = e.InnerException;
                }
                Exceptions.SetError(e);
                return null;
            }
            return result;
        }
    }
}
