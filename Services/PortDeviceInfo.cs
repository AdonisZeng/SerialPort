using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace SerialPort.Services
{
    /// <summary>
    /// 端口列表项：端口号 + 设备描述（如 "USB-SERIAL CH340"）。
    /// 下拉框 Items 存该对象，显示由 ToString 决定，连接逻辑只读 PortName（不依赖显示格式）。
    /// </summary>
    public sealed class SerialPortItem
    {
        public string PortName { get; }
        public string Description { get; }

        public SerialPortItem(string portName, string description)
        {
            PortName = portName;
            Description = description;
        }

        public override string ToString() =>
            string.IsNullOrEmpty(Description) ? PortName : $"{PortName} ({Description})";
    }

    /// <summary>
    /// 通过 WMI 查询 Win32_PnPEntity 获取串口对应的设备描述（设备管理器中的显示名）。
    /// 查询约几十毫秒，应在后台线程调用；失败时返回空字典，UI 退化为纯端口号显示。
    /// </summary>
    public static class PortDeviceInfo
    {
        /// <summary>查询系统中所有串口的设备描述，返回 端口名 → 描述 字典（查询失败返回空字典）。</summary>
        public static Dictionary<string, string> GetDescriptions()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%)'"))
                using (var collection = searcher.Get())
                {
                    foreach (ManagementBaseObject mo in collection)
                    {
                        string name = mo["Name"] as string;
                        if (string.IsNullOrEmpty(name)) continue;
                        // 设备管理器显示名形如 "USB-SERIAL CH340 (COM3)"：取最后一个 "(COM" 解析
                        int idx = name.LastIndexOf("(COM", StringComparison.OrdinalIgnoreCase);
                        if (idx < 0 || !name.EndsWith(")", StringComparison.Ordinal)) continue;
                        string port = name.Substring(idx + 1, name.Length - idx - 2);   // "COM3" / "COM10"
                        if (port.Length > 3 && port.Substring(3).All(char.IsDigit))
                        {
                            string description = name.Substring(0, idx).Trim();
                            result[port] = description;
                        }
                    }
                }
            }
            catch
            {
                // WMI 查询失败（权限不足 / 服务异常）：返回空字典，功能不受影响
            }
            return result;
        }
    }
}
