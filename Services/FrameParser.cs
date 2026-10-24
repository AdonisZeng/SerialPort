using System;
using System.Collections.Generic;

namespace SerialPort.Services
{
    /// <summary>帧校验算法。</summary>
    public enum FrameChecksum
    {
        /// <summary>无校验（仅显示原始帧）。</summary>
        None,

        /// <summary>CRC16-Modbus（多项式 0x8005 反射 0xA001，初值 0xFFFF，低字节在前）。</summary>
        Crc16Modbus,

        /// <summary>字节和（所有字节相加取低 8 位）。</summary>
        Sum8
    }

    /// <summary>
    /// 通用协议帧解析器：按可配置帧头 / 帧尾（hex，可留空）从字节流中提取完整帧。
    /// 状态保持（跨数据块），把接收数据块喂入即可；单线程使用（UI 线程）。
    ///
    /// 定界规则：
    /// - 指定帧尾：帧 = 帧头（若配置）+ 任意内容 + 帧尾；帧尾前的孤立数据在有帧头时被丢弃，
    ///   无帧头时从解析起点 / 上一帧结束后收集。
    /// - 仅指定帧头（无帧尾）：以帧头定界——出现完整帧头时完成上一帧并开始新帧。
    /// - 两者都未指定：无法定界（调用方应拦截，不产生帧）。
    ///
    /// 校验规则：若帧尾长度恰好等于校验输出长度（CRC16 = 2 字节，Sum8 = 1 字节），
    /// 则把帧尾视为预期校验值（校验范围 = 帧去掉帧尾，CRC16 低字节在前）并给出通过 / 失败；
    /// 否则对整帧计算并仅显示参考值。
    /// </summary>
    public sealed class FrameParser
    {
        private byte[] _header;   // 帧头（null = 未配置）
        private byte[] _footer;   // 帧尾（null = 未配置）
        private FrameChecksum _checksum;
        private readonly List<byte> _buffer = new List<byte>();   // 当前帧收集缓冲
        private bool _inFrame;      // 帧尾定界模式下：已识别帧头（或无帧头时的收集状态）
        // 两种定界模式共用的"候选窗口"：始终是 _header 的一个前缀（长度 0.._header.Length-1）。
        // 帧头自重叠（如帧头 AA AA 55、帧体出现 AA AA AA 55）时必须对窗口整体回退重扫，
        // 只回退"当前字节"会漏掉窗口中间的帧头，故不匹配时逐个出队而不是整段丢弃。
        private readonly List<byte> _partial = new List<byte>();

        /// <summary>单帧收集上限：帧尾 / 下一帧头迟迟不出现时丢弃未完成帧，防止内存无界增长。</summary>
        private const int MaxFrameBytes = 64 * 1024;

        /// <summary>更新帧格式配置并复位解析状态（丢弃未完成帧）。</summary>
        public void Configure(byte[] header, byte[] footer, FrameChecksum checksum)
        {
            _header = header == null || header.Length == 0 ? null : header;
            _footer = footer == null || footer.Length == 0 ? null : footer;
            _checksum = checksum;
            Reset();
        }

        /// <summary>复位解析状态（丢弃未完成帧）。</summary>
        public void Reset()
        {
            _buffer.Clear();
            _partial.Clear();
            _inFrame = false;
        }

        /// <summary>
        /// 输出缓冲区中尚未定界的残留帧（流结束 / 串口断开时调用），并复位解析状态。
        /// 仅帧头定界模式输出：该模式本就没有帧尾，"流结束"即一帧结束，末帧只能靠
        /// 下一个帧头或此方法收尾。帧尾定界模式的缓冲是"还没等到帧尾"的半帧，
        /// 按定义不成立，输出只会凭空多一行脏数据（且校验必然失败），故直接丢弃。
        /// </summary>
        public List<Frame> Flush()
        {
            var frames = new List<Frame>();
            if (_footer == null && _buffer.Count > 0)
                frames.Add(MakeFrame(_buffer.ToArray()));
            Reset();
            return frames;
        }

        /// <summary>解析出的完整帧。</summary>
        public sealed class Frame
        {
            /// <summary>完整帧字节（含帧头 / 帧尾）。</summary>
            public byte[] Data;

            /// <summary>校验是否通过（无校验或仅参考值时恒 true）。</summary>
            public bool ChecksumOk;

            /// <summary>校验说明（如 "CRC16: 0xC4B3" / "无校验"）。</summary>
            public string ChecksumText;
        }

        /// <summary>喂入一段接收数据，返回本段解析出的完整帧（可能为空列表）。</summary>
        public List<Frame> Push(byte[] data)
        {
            var frames = new List<Frame>();
            if (data == null || data.Length == 0) return frames;
            if (_footer != null)
                PushFooterMode(data, frames);
            else if (_header != null)
                PushHeaderMode(data, frames);
            return frames;
        }

        // ============ 帧尾定界模式 ============

        /// <summary>
        /// 帧尾定界模式：帧尾出现即完成一帧。帧头匹配与帧头定界模式共用候选窗口语义，
        /// 才能识别帧头自重叠（朴素单指针回退会漏掉窗口中间的帧头）。
        /// </summary>
        private void PushFooterMode(byte[] data, List<Frame> frames)
        {
            foreach (byte b in data)
            {
                if (_inFrame)
                {
                    _buffer.Add(b);
                    if (EndsWithFooter())
                    {
                        frames.Add(MakeFrame(_buffer.ToArray()));
                        Reset();
                        continue;
                    }
                    // 帧尾迟迟不出现（配置不当 / 数据流异常）：丢弃未完成帧，重新等待帧头，防止缓冲无界增长
                    if (_buffer.Count > MaxFrameBytes) Reset();
                    continue;
                }

                if (_header == null)
                {
                    // 无帧头：从解析起点 / 上一帧结束后开始收集
                    _buffer.Add(b);
                    _inFrame = true;
                    continue;
                }

                // 有帧头：候选窗口先于帧开始积累，不再是帧头前缀就逐字节出队丢弃
                // （帧头前的孤立数据不构成帧）。与帧头定界模式同一套语义，才能识别
                // 帧头自重叠（帧头 AA AA 55，流中出现 AA AA AA 55）
                _partial.Add(b);
                while (_partial.Count > 0 && !IsHeaderPrefix())
                    _partial.RemoveAt(0);

                if (_partial.Count == _header.Length)
                {
                    _buffer.Clear();
                    _buffer.AddRange(_header);
                    _partial.Clear();
                    _inFrame = true;
                }
            }
        }

        private bool EndsWithFooter()
        {
            int count = _buffer.Count;
            if (count < _footer.Length) return false;
            for (int i = 0; i < _footer.Length; i++)
                if (_buffer[count - _footer.Length + i] != _footer[i]) return false;
            return true;
        }

        // ============ 帧头定界模式（无帧尾） ============

        private void PushHeaderMode(byte[] data, List<Frame> frames)
        {
            foreach (byte b in data)
            {
                // 候选窗口：新字节追加到窗口末尾后整体重新判定。
                // 只要窗口不再是帧头前缀，就把队首字节出队并计入帧体（帧尚未开始时丢弃），
                // 再对剩余窗口继续判定——这样帧头自重叠（帧头 AA AA 55、
                // 帧体出现 AA AA AA 55）时也不会漏掉窗口中间那个帧头。
                // 未判定前不能整段丢弃：帧体中与帧头首字节相同的字节会被静默吞掉。
                _partial.Add(b);
                while (_partial.Count > 0 && !IsHeaderPrefix())
                {
                    byte dropped = _partial[0];
                    _partial.RemoveAt(0);
                    if (_buffer.Count == 0) continue;   // 帧尚未开始：帧头前的孤立字节丢弃
                    _buffer.Add(dropped);
                    // 帧头迟迟不出现：丢弃未完成帧，重新等待帧头，防止缓冲无界增长。
                    // Reset 会清空 _partial，while 条件随之退出，无需额外 break。
                    if (_buffer.Count > MaxFrameBytes) Reset();
                }

                // 窗口凑满即完整帧头（单字节帧头在本次迭代内立即成帧）
                if (_partial.Count == _header.Length) StartNewFrame(frames);
            }
        }

        /// <summary>当前候选窗口是否仍是帧头的前缀（出队回退的基础判定）。
        /// 前置条件：处理完每个字节后窗口长度恒 &lt; _header.Length，故此处 _header[i] 不会越界。</summary>
        private bool IsHeaderPrefix()
        {
            for (int i = 0; i < _partial.Count; i++)
                if (_partial[i] != _header[i]) return false;
            return true;
        }

        /// <summary>完整帧头已识别：完成上一帧（含其帧头），新帧从当前帧头开始。
        /// 候选窗口即新帧头本身，直接丢弃。</summary>
        private void StartNewFrame(List<Frame> frames)
        {
            if (_buffer.Count > 0)
            {
                frames.Add(MakeFrame(_buffer.ToArray()));
                _buffer.Clear();
            }
            _buffer.AddRange(_header);
            _partial.Clear();
        }

        // ============ 帧构造与校验 ============

        private Frame MakeFrame(byte[] frame)
        {
            var f = new Frame { Data = frame };
            if (_checksum == FrameChecksum.None)
            {
                f.ChecksumOk = true;
                f.ChecksumText = "无校验";
                return f;
            }

            int crcBytes = _checksum == FrameChecksum.Crc16Modbus ? 2 : 1;
            // 帧尾即校验值时按"帧尾 = 预期校验值"校验；长度不足校验字节数的帧
            //（如仅在帧头定界模式才会出现的极短帧）走整帧参考值分支，避免取下标越界
            if (_footer != null && _footer.Length == crcBytes && frame.Length >= crcBytes)
            {
                // 帧尾即预期校验值：校验范围 = 帧去掉帧尾
                int dataLen = frame.Length - crcBytes;
                if (_checksum == FrameChecksum.Crc16Modbus)
                {
                    ushort actual = Crc16Modbus(frame, 0, dataLen);
                    f.ChecksumText = string.Format("CRC16: 0x{0:X4}", actual);
                    f.ChecksumOk = frame[dataLen] == (byte)actual && frame[dataLen + 1] == (byte)(actual >> 8);
                }
                else
                {
                    byte actual = Sum8(frame, 0, dataLen);
                    f.ChecksumText = string.Format("Sum8: 0x{0:X2}", actual);
                    f.ChecksumOk = frame[dataLen] == actual;
                }
            }
            else
            {
                // 帧尾与校验长度不符（或帧头定界模式）：整帧计算，仅显示参考值
                if (_checksum == FrameChecksum.Crc16Modbus)
                    f.ChecksumText = string.Format("CRC16: 0x{0:X4}", Crc16Modbus(frame, 0, frame.Length));
                else
                    f.ChecksumText = string.Format("Sum8: 0x{0:X2}", Sum8(frame, 0, frame.Length));
                f.ChecksumOk = true;
            }
            return f;
        }

        /// <summary>CRC16-Modbus（初值 0xFFFF，反射多项式 0xA001）。</summary>
        public static ushort Crc16Modbus(byte[] data, int start, int count)
        {
            ushort crc = 0xFFFF;
            for (int i = start; i < start + count; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                    crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
            return crc;
        }

        /// <summary>字节和（取低 8 位）。</summary>
        public static byte Sum8(byte[] data, int start, int count)
        {
            int sum = 0;
            for (int i = start; i < start + count; i++)
                sum += data[i];
            return (byte)sum;
        }
    }
}
