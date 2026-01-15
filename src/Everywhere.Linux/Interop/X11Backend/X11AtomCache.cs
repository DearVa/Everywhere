using System.Collections.Concurrent;
using System.Linq;
using X11;

namespace Everywhere.Linux.Interop.X11Backend
{
    public sealed class AtomCache
    {
        private readonly IntPtr _display;
        private readonly ConcurrentDictionary<string, Atom> _cache = new(StringComparer.Ordinal);

        public AtomCache(IntPtr display, IEnumerable<string>? predefinedAtoms = null)
        {
            _display = display;

            if (_display != IntPtr.Zero && predefinedAtoms != null)
            {
                var names = predefinedAtoms.Where(n => !string.IsNullOrEmpty(n)).Distinct().ToArray();
                if (names.Length > 0)
                {
                    foreach (var name in names)
                    {
                        var atom = Xlib.XInternAtom(_display, name, false);
                        if (atom != Atom.None)
                            _cache.TryAdd(name, atom);
                    }
                }
            }
        }

        public Atom GetAtom(string name, bool onlyIfExists = false)
        {
            if (string.IsNullOrEmpty(name) || _display == IntPtr.Zero)
                return Atom.None;

            if (_cache.TryGetValue(name, out var atom))
                return atom;

            var a = Xlib.XInternAtom(_display, name, onlyIfExists);
            if (a != Atom.None)
                _cache.TryAdd(name, a);
            return a;
        }
    }
}