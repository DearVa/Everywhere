using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Everywhere.Windows.Interop;

[GeneratedComInterface]
[Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
internal partial interface IDirect3DDxgiInterfaceAccess
{
    nint GetInterface(in Guid iid);
}
