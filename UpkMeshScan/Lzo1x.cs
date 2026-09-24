namespace UpkMeshScan;

/// <summary>
/// Bounds-checked managed LZO1X decompressor (the public LZO1X stream format).
/// Throws InvalidDataException on malformed input instead of reading/writing out of range.
/// </summary>
public static class Lzo1x
{
    public static void Decompress(byte[] src, int srcOff, int srcLen, byte[] dst, int dstOff, int dstLen)
        => new State(src, srcOff, srcLen, dst, dstOff, dstLen).Run();

    private sealed class State
    {
        readonly byte[] s, d;
        readonly int sEnd, dStart, dEnd;
        int ip, op;

        public State(byte[] src, int srcOff, int srcLen, byte[] dst, int dstOff, int dstLen)
        {
            if (srcOff < 0 || srcLen < 0 || srcOff + srcLen > src.Length) throw new ArgumentOutOfRangeException(nameof(srcLen));
            if (dstOff < 0 || dstLen < 0 || dstOff + dstLen > dst.Length) throw new ArgumentOutOfRangeException(nameof(dstLen));
            s = src; d = dst; ip = srcOff; sEnd = srcOff + srcLen; op = dStart = dstOff; dEnd = dstOff + dstLen;
        }

        int In() { if (ip >= sEnd) throw new InvalidDataException("LZO: input overrun"); return s[ip++]; }

        void Lit(int n)
        {
            if (ip + n > sEnd || op + n > dEnd) throw new InvalidDataException("LZO: literal overrun");
            Buffer.BlockCopy(s, ip, d, op, n); ip += n; op += n;
        }

        void Copy(int from, int n)
        {
            if (from < dStart || op + n > dEnd) throw new InvalidDataException("LZO: match out of range");
            for (int i = 0; i < n; i++) d[op++] = d[from++]; // byte-wise on purpose: overlapping copies
        }

        int Ext(int baseVal)
        {
            int v = 0;
            while (true) { int b = In(); if (b != 0) return v + baseVal + b; v += 255; if (v > (1 << 28)) throw new InvalidDataException("LZO: bad length"); }
        }

        public void Run()
        {
            int t, mPos;
            if (sEnd - ip <= 0) throw new InvalidDataException("LZO: empty input");

            t = s[ip];
            if (t > 17)
            {
                ip++; t -= 17;
                if (t < 4) goto match_next;
                Lit(t);
                goto first_literal_run;
            }

        loop:
            t = In();
            if (t >= 16) goto match;
            if (t == 0) t = Ext(15);
            Lit(t + 3);

        first_literal_run:
            t = In();
            if (t >= 16) goto match;
            mPos = op - (1 + 0x0800) - (t >> 2);
            mPos -= In() << 2;
            Copy(mPos, 3);
            goto match_done;

        match:
            if (t >= 64)
            {
                mPos = op - 1 - ((t >> 2) & 7);
                mPos -= In() << 3;
                t = (t >> 5) - 1;
            }
            else if (t >= 32)
            {
                t &= 31;
                if (t == 0) t = Ext(31);
                int lo = In(), hi = In();
                mPos = op - 1 - ((lo | (hi << 8)) >> 2);
            }
            else if (t >= 16)
            {
                mPos = op - ((t & 8) << 11);
                t &= 7;
                if (t == 0) t = Ext(7);
                int lo = In(), hi = In();
                mPos -= (lo | (hi << 8)) >> 2;
                if (mPos == op) goto eof;
                mPos -= 0x4000;
            }
            else
            {
                mPos = op - 1 - (t >> 2);
                mPos -= In() << 2;
                Copy(mPos, 2);
                goto match_done;
            }
            Copy(mPos, t + 2);

        match_done:
            t = s[ip - 2] & 3;
            if (t == 0) goto loop;

        match_next:
            Lit(t);
            t = In();
            goto match;

        eof:
            if (op != dEnd) throw new InvalidDataException($"LZO: produced {op - dStart} bytes, expected {dEnd - dStart}");
        }
    }
}
