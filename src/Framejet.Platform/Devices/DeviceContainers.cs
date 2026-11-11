using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Framejet.Platform.Devices
{
    public static class DeviceContainers
    {
        private const int CR_SUCCESS = 0;
        private const int CR_BUFFER_SMALL = 0x1A;
        private const uint DEVPROP_TYPE_GUID = 0x0D;
        private const uint DEVPROP_TYPE_STRING = 0x12;

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        private delegate int PropertyCall(byte[] buffer, ref uint size, out uint type);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int CM_Get_Device_Interface_PropertyW(string pszDeviceInterface, ref DEVPROPKEY propertyKey,
            out uint propertyType, byte[] propertyBuffer, ref uint propertyBufferSize, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int CM_Get_DevNode_PropertyW(uint dnDevInst, ref DEVPROPKEY propertyKey,
            out uint propertyType, byte[] propertyBuffer, ref uint propertyBufferSize, uint ulFlags);

        /// <summary>Windows device container of a DirectShow DevicePath, or Guid.Empty when unknown.</summary>
        public static Guid ForDirectShowDevicePath(string devicePath)
        {
            try
            {
                if (string.IsNullOrEmpty(devicePath)) return Guid.Empty;
                const string prefix = "@device:pnp:";
                string path = devicePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    ? devicePath.Substring(prefix.Length)
                    : devicePath;

                string instanceId = InstanceIdOf(path);
                if (instanceId == null)
                {
                    int cut = path.LastIndexOf('\\');
                    if (cut > 0) instanceId = InstanceIdOf(path.Substring(0, cut));
                }
                if (instanceId == null) return Guid.Empty;

                if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != CR_SUCCESS) return Guid.Empty;

                byte[] data = ReadProperty((byte[] buf, ref uint size, out uint type) =>
                {
                    var key = new DEVPROPKEY { fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), pid = 2 };
                    return CM_Get_DevNode_PropertyW(devInst, ref key, out type, buf, ref size, 0);
                }, DEVPROP_TYPE_GUID);
                return data != null && data.Length == 16 ? new Guid(data) : Guid.Empty;
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                return Guid.Empty;
            }
        }

        private static string InstanceIdOf(string interfacePath)
        {
            byte[] data = ReadProperty((byte[] buf, ref uint size, out uint type) =>
            {
                var key = new DEVPROPKEY { fmtid = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), pid = 256 };
                return CM_Get_Device_Interface_PropertyW(interfacePath, ref key, out type, buf, ref size, 0);
            }, DEVPROP_TYPE_STRING);
            if (data == null || data.Length < 2) return null;
            return Encoding.Unicode.GetString(data).TrimEnd('\0');
        }

        // Size query first (empty buffer yields CR_BUFFER_SMALL plus the required size), then the real read.
        private static byte[] ReadProperty(PropertyCall call, uint expectedType)
        {
            uint size = 0;
            int cr = call(new byte[0], ref size, out uint type);
            if (cr != CR_BUFFER_SMALL || size == 0) return null;
            var buffer = new byte[size];
            cr = call(buffer, ref size, out type);
            if (cr != CR_SUCCESS || type != expectedType) return null;
            if (size != buffer.Length) Array.Resize(ref buffer, (int)size);
            return buffer;
        }
    }
}
