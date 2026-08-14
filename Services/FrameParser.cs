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
        private int _headerMatch;   // 帧头匹配进度（帧头定界模式下等于已匹配字节数）
        private bool _inFrame;      // 帧尾定界模式下：已识别帧头（或无帧头时的收集状态）

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
            _headerMatch = 0;
            _inFrame = false;
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
                        _buffer.Clear();
                        _inFrame = false;
                    }
                    continue;
                }

                if (_header == null)
                {
                    // 无帧头：从解析起点 / 上一帧结束后开始收集
                    _buffer.Add(b);
                    _inFrame = true;
                    continue;
                }

                // 有帧头：先匹配帧头
                if (b == _header[_headerMatch])
                {
                    _headerMatch++;
                    if (_headerMatch == _header.Length)
                    {
                        _buffer.Clear();
                        _buffer.AddRange(_header);
                        _inFrame = true;
                        _headerMatch = 0;
                    }
                }
                else
                {
                    // 部分匹配失败：回退比较当前字节与帧头首字节（重复首字节模式如 AA AA 55 不丢帧头），
                    // 不匹配则丢弃该字节（帧头前的孤立数据不构成帧）
                    _headerMatch = b == _header[0] ? 1 : 0;
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
                if (b == _header[_headerMatch])
                {
                    _headerMatch++;
                    if (_headerMatch == _header.Length)
                    {
                        // 出现完整帧头：完成上一帧（含其帧头），新帧从当前帧头开始
                        if (_buffer.Count > 0)
                        {
                            frames.Add(MakeFrame(_buffer.ToArray()));
                            _buffer.Clear();
                        }
                        _buffer.AddRange(_header);
                        _headerMatch = 0;
                    }
                }
                else
                {
                    // 部分匹配失败：回退比较当前字节与帧头首字节（重复首字节模式不丢帧头）
                    _headerMatch = b == _header[0] ? 1 : 0;
                    if (_buffer.Count > 0) _buffer.Add(b);   // 帧收集进行中：计入帧体
                    // 尚未开始帧：孤立字节直接丢弃
                }
            }
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
            if (_footer != null && _footer.Length == crcBytes)
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
