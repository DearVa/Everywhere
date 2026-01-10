using System.Collections.Concurrent;
using X11;

namespace Everywhere.Linux.Interop
{
    /// <summary>
    /// Simple cache for X atoms.
    /// </summary>
    internal sealed class AtomCache
    {
        private readonly IntPtr _display;
        private readonly ConcurrentDictionary<string, Atom> _cache = new(StringComparer.Ordinal);

        public AtomCache(IntPtr display)
        {
            _display = display;
        }
        public Atom GetAtom(string name, bool onlyIfExists = false)
        {
            if (string.IsNullOrEmpty(name) || _display == IntPtr.Zero)
                return Atom.None;

            if (onlyIfExists)
            {
                if (_cache.TryGetValue(name, out var atom))
                    return atom;

                var a = Xlib.XInternAtom(_display, name, true);
                if (a != Atom.None)
                    _cache.TryAdd(name, a);
                return a;
            }

            return _cache.GetOrAdd(name, n => Xlib.XInternAtom(_display, n, false));
        }
    }
}
