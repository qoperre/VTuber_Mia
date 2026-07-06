using System;
using System.Text;

namespace uOSC
{

public static class Reader
{
    public static string ParseString(byte[] buf, ref int pos)
    {
        int size = 0;
        int bufSize = buf.Length;
        // 버퍼 끝을 넘어서 읽지 않도록 경계를 검사합니다.
        // (OSC 형식이 아닌 패킷이 들어오면 null 종료 바이트가 없어 배열 초과가 발생합니다.)
        for (; pos + size < bufSize && buf[pos + size] != 0; ++size);
        if (pos + size >= bufSize)
        {
            // 유효한 null 종료 문자열이 아니므로 잘못된 패킷으로 간주합니다.
            throw new InvalidOperationException("Received data is not a valid OSC packet (unterminated string).");
        }
        var value = Encoding.UTF8.GetString(buf, pos, size);
        pos += Util.GetStringAlignedSize(size);
        return value;
    }

    public static int ParseInt(byte[] buf, ref int pos)
    {
        Array.Reverse(buf, pos, 4);
        var value = BitConverter.ToInt32(buf, pos);
        pos += 4;
        return value;
    }

    public static float ParseFloat(byte[] buf, ref int pos)
    {
        Array.Reverse(buf, pos, 4);
        var value = BitConverter.ToSingle(buf, pos);
        pos += 4;
        return value;
    }

    public static byte[] ParseBlob(byte[] buf, ref int pos)
    {
        var size = ParseInt(buf, ref pos);
        var value = new byte[size];
        Buffer.BlockCopy(buf, pos, value, 0, size);
        pos += Util.GetBufferAlignedSize(size);
        return value;
    }

    public static ulong ParseTimetag(byte[] buf, ref int pos)
    {
        Array.Reverse(buf, pos, 8);
        var value = BitConverter.ToUInt64(buf, pos);
        pos += 8;
        return value;
    }
}

}