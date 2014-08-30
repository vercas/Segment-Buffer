/*
 * This code is subject to the terms of the vLicense: http://www.vercas.com/license
 * 
 *  Copyright (c) 2014 Alexandru-Mihai Maftei
 * 
 *  Permission is hereby granted, free of charge, to any person obtaining a copy
 *  of this software and associated documentation files (the “Software”), to deal
 *  in the Software without restriction, including without limitation the rights
 *  to use, copy, modify, merge, publish, distribute, and/or sell
 *  copies of the Software, and to permit persons to whom the Software is
 *  furnished to do so, subject to the following conditions:
 *  
 *  The above copyright notice and this license shall be included whole and
 *  unaltered in all copies, substantial portions or modified versions of
 *  the Software.
 *  Any closed source distribution of the Software shall properly credit the
 *  copyright holders unless such a feature is impossible.
 *  Any modified versions of the Software must be available to the public,
 *  as is the Software. Should the modifications be substantial, the author
 *  may append their copyright notice to the current ones.
 * 
 *  THE SOFTWARE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 *  IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 *  FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 *  AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 *  LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 *  OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 *  THE SOFTWARE.
 * 
 */

#if NET_4_5_PLUS
#define SEGMENTBUFFER_USEASYNC
#endif

using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace System.IO //  <-- Namespace may be changed.
{
    /// <summary>
    /// Presents arbitrary chunks of bytes as a continuous stream.
    /// </summary>
    [CLSCompliant(true)]    //  <-- This attribute may be removed.
    public partial class SegmentBuffer
        : Stream
    {
        #region Constants

        /// <summary>
        /// A recommended default segment length.
        /// </summary>
        public const long DefaultSegmentLength = (1L << 16) - 20L - 4L - 64L;

        private static byte[] EmptyByteArray = new byte[0];

        #endregion

        #region IDisposable Implementation

        private int _disposalStatus = 0;

        /// <summary>
        /// Releases the unmanaged resources used by the <see cref="System.IO.SegmentBuffer"/> and optionally releases the managed resources.
        /// </summary>
        /// <param name="disposing"></param>
        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposalStatus, -1) != -1)
            {
                this.segs = null;   //  I see that setting all references to null is common practice in the .NET source code, so I shall do the same for it will not hurt.
            }
        }

        /// <summary>
        /// Marks the the write buffer as finished, resetting the position within the buffer and allowing it to be read back.
        /// </summary>
        /// <exception cref="System.ObjectDisposedException">Thrown when the buffer is disposed.</exception>
        /// <exception cref="System.InvalidOperationException">Thrown when the buffer is in read mode or is already finished.</exception>
        public void Finish()
        {
            if ((mode & SegmentBufferModes.Write) == 0)
                throw new InvalidOperationException("Buffer is in read mode.");

            if (Interlocked.CompareExchange(ref _disposalStatus, 1, 0) == 0)
            {
                this.Position = 0;

                return;// new ArraySegment<byte>(this.data, this.off, (int)this.len);
            }
            else if (this._disposalStatus == -1)
                throw new ObjectDisposedException(this.GetType().FullName);
            else if (this._disposalStatus == 1)
                throw new InvalidOperationException("Buffer is already finished.");

            throw new Exception("wat");
        }
        
        /// <summary>
        /// Gets a value indicating whether the buffer is disposed or not.
        /// </summary>
        public Boolean Disposed { get { return this._disposalStatus == -1; } }

        /// <summary>
        /// Gets a value indicating whether the buffer is finished or not.
        /// <para>A finished buffer has had all intended write operations on it done and is ready for reading back.</para>
        /// </summary>
        public Boolean Finished { get { return this._disposalStatus == 1; } }

        #endregion

        #region Fields and Properties

        private long pos = -1, len, cap, newSegLength, segCurPos, segCurProg;
        private int segCurIndex = 0;
        private ByteArraySegment segCur = null;
        private List<ByteArraySegment> segs;

        private SegmentBufferModes mode;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="System.IO.SegmentBuffer"/> class with the specified mode of operation, initial data and initial position.
        /// </summary>
        /// <remarks>
        /// When using <see cref="System.IO.SegmentBufferModes.Read"/> mode, initial data has to be specified.
        /// <para>When using <see cref="System.IO.SegmentBufferModes.Write"/> mode and data is null, the initial back-end data will be sized exponentially to contain the initial position and still have some space after it to write (at least one byte).</para>
        /// <para>The initial value for the new segment length is based on a common page size, minus the extra data contained in the segment class. Padding is accounted for as well and some extra bytes are removed just to be sure.</para>
        /// </remarks>
        /// <param name="mode">Buffer operation mode(s).</param>
        /// <param name="data">A sequence of byte array segments representing initial data for the buffer.</param>
        /// <param name="pos">Initian position in the buffer.</param>
        /// <param name="segmentLength">optional; The length of the data segments which will be created, if necessary.</param>
        /// <exception cref="System.ArgumentNullException">Thrown when the given initial <paramref name="data"/> is null when the buffer <paramref name="mode"/> is <see cref="System.IO.SegmentBufferModes.Read"/>.</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">Thrown when there is a contradiction in the buffer <paramref name="mode"/>s -or- the initial <paramref name="data"/> enumeration contains no elements or no bytes when the buffer <paramref name="mode"/> is <see cref="System.IO.SegmentBufferModes.Read"/> -or- the initial position (<paramref name="pos"/>) is negative or beyond the end of the initial data when un-expandable -or- <paramref name="segmentLength"/> is not strictly positive when used.</exception>
        public SegmentBuffer(SegmentBufferModes mode, IEnumerable<ArraySegment<byte>> data, long pos, long segmentLength = DefaultSegmentLength)
        {
            if (pos < 0L)
                throw new ArgumentOutOfRangeException("pos", pos, "Position cannot be negative.");

            this.newSegLength = segmentLength;
            this.mode = mode;

            if ((mode & SegmentBufferModes.Write) != 0)
            {
                if ((mode & SegmentBufferModes.Read) != 0)
                    throw new ArgumentOutOfRangeException("mode", mode, "Cannot use read and write modes at the same time.");

                this.len = pos; //  Normally, it would start at length 0, but the position must not exceed the length.

                if (data == null)   //  If no initial data was specified for a write buffer, the back-end segment sequence is created large enough to contain the initial position.
                {
                    if (segmentLength < 1L)
                        throw new ArgumentException("Newly-created segment length must be strictly positive.", "segmentLength");

                    long segCnt = pos / segmentLength + 1;  //  This is so free room will always exist.

                    this.segs = new List<ByteArraySegment>((int)segCnt);    //  Initial capacity is already known - this may not need enlarging.
                    this.cap = segCnt * segmentLength;

                    for (; segCnt > 0L; segCnt--) this.segs.Add(new ByteArraySegment(new byte[segmentLength], 0L, segmentLength, false));  //  Create the required segments.
                }
                else
                {
                    if ((mode & SegmentBufferModes.PreserveInitialData) != 0 && data.Sum(arseg => (long)arseg.Count) > 0L)  //  Initial buffer is preserved, but only if there is any data in it.
                    {
                        this.segs = new List<ByteArraySegment>(data.Select(arseg => (ByteArraySegment)arseg));
                        this.cap = this.segs.Sum(seg => seg.cnt);
                    }
                    else if ((mode & SegmentBufferModes.AutoEnlarge) != 0)  //  Auto-enlarging allowed with initial data. It will be copied to another buffer if possible.
                    {
                        if (segmentLength < 1L)
                            throw new ArgumentException("Newly-created segment length must be strictly positive.", "segmentLength");

                        this.segs = new List<ByteArraySegment>(ByteArraySegment.Copy(data));    //  First, all the initial data must be accomodated.
                        this.cap = this.segs.Sum(seg => seg.cnt);   //  Capacity of initial data.

                        if (this.cap <= pos)  //  More segments are required to contain the initial position.
                        {
                            long segCnt = 0L;
                            if (Math.DivRem(pos - this.cap, segmentLength, out segCnt) != 0L) segCnt++; //  If the remained of the division is not 0, one more segment is needed.
                            this.cap += segCnt * segmentLength; //  Capacity increased.

                            for (segCnt -= this.segs.Count; segCnt > 0L; segCnt--) this.segs.Add(new ByteArraySegment(new byte[segmentLength], 0L, segmentLength, false));  //  Fill any remaining required segments
                        }
                    }
                    else    //  There is initial data and auto-enlarging the buffer is disallowed. Gotta make sure the position is within the buffer boundaries.
                    {
                        this.cap = data.Sum(seg => (long)seg.Count);    //  Converting each count to a long so the addition result will not overflow.

                        if (this.cap < 1L)
                            throw new ArgumentException("Data segment length must be greater than 0 (zero).", "data");
                        if (pos >= this.cap)
                            throw new ArgumentOutOfRangeException("pos", pos, "Position is beyond initial data segment boundaries.");

                        this.segs = new List<ByteArraySegment>(ByteArraySegment.Copy(data)); //  Copies are created.
                    }
                }
            }
            else if ((mode & SegmentBufferModes.Read) != 0)
            {
                if (data == null)
                    throw new ArgumentNullException("data", "Initial data segment cannot be null when read mode is specified.");

                this.segs = new List<ByteArraySegment>(data.Select(arseg => (ByteArraySegment)arseg));
                if (this.segs.Count == 0)
                    throw new ArgumentException("Initial data enumeration must contain at least one element.", "data");

                this.cap = this.len = this.segs.Sum(seg => seg.cnt);
                if (this.len == 0L)
                    throw new ArgumentException("Initial data enumeration must contain at least one byte.", "data");
            }
            else
                throw new ArgumentException("Neither read more nor write mode are specified.");

            this.Position = pos;
        }

        #endregion

        #region Stream Implementation

        /// <summary>
        /// Gets a value indicating whether the buffer is in read mode or not.
        /// </summary>
        public override bool CanRead
        {
            get { return this._disposalStatus != -1 && (this._disposalStatus == 1 || (this.mode & SegmentBufferModes.Read) != 0); }
        }

        /// <summary>
        /// Gets a value indicating whether it is possible to seek through the buffer or not.
        /// </summary>
        public override bool CanSeek
        {
            get { return true; }
        }

        /// <summary>
        /// Gets a value indicating whether the buffer is in write mode or not.
        /// </summary>
        public override bool CanWrite
        {
            get { return this._disposalStatus == 0 && (this.mode & SegmentBufferModes.Write) != 0; }
        }

        /// <summary>
        /// Does nothing.
        /// </summary>
        public override void Flush()
        {
            //  throw new NotImplementedException();
            //  Nothing.
        }

        /// <summary>
        /// Gets the length of the buffer.
        /// </summary>
        public override long Length
        {
            get { return this.len; }
        }

        /// <summary>
        /// Gets the total capacity of the underlying data segments.
        /// </summary>
        public long Capacity
        {
            get { return this.cap; }
        }

        /// <summary>
        /// Gets or sets the position within the buffer.
        /// </summary>
        /// <exception cref="System.ObjectDisposedException">Thrown when the buffer is disposed.</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">Thrown when the given index is negative or beyond the end of the buffer.</exception>
        public override long Position
        {
            get
            {
                return this.pos;
            }
            set
            {
                if (this._disposalStatus == -1)
                    throw new ObjectDisposedException(this.GetType().FullName);

                if (value < 0L)
                    throw new ArgumentOutOfRangeException("Position must not be negative.");
                if (value > this.len)
                    throw new ArgumentOutOfRangeException("Position is beyond end of buffer.");

                /* I feel this code requires a thorough explaination.
                 * Here's the deal. When changing the position within the stream, the index of the segment
                 * which contains the position must be known. I also store the "segment progress", which
                 * is basically the amount of bytes before the current segment.
                 * This allows me to find the segment of the new position based on what is know about the
                 * current position!
                 * If the new position is in front of the current one, I just look ahead from the current
                 * segment until the value fits in the segment relative to the number of bytes before that
                 * segment.
                 * If the new position is before the current one, I look behind until the segment progress
                 * is less than or equal to the new position - that segment contains the new position.
                 * Also, when the position is equal to the buffer's capacity, there is no next segment to
                 * move to. However, they may be one when the position is equal to the length, because the
                 * length may be lower than or equal to the capacity.
                 */

                if (value > this.pos)
                {
                    for (int i = this.segCurIndex; i < this.segs.Count; i++)
                        if (value - this.segCurProg < this.segs[i].cnt || (value - this.segCurProg == this.segs[i].cnt && value == this.cap))
                        {
                            this.segCurIndex = i;
                            this.segCur = this.segs[i];
                            this.segCurPos = value - this.segCurProg;

                            break;
                        }
                        else
                            this.segCurProg += this.segs[i].cnt;
                }
                else if (value < this.pos)
                {
                    for (int i = this.segCurIndex; i >= 0; i--)
                        if (value - this.segCurProg >= 0)
                        {
                            this.segCurIndex = i;
                            this.segCur = this.segs[i];
                            this.segCurPos = value - this.segCurProg;

                            break;
                        }
                        else
                            this.segCurProg -= this.segs[i - 1].cnt; /*  I need to subtract the length of the PREVIOUS segment.
                     *  This should never thrown an exception because the progress of the first segment should be 0, and
                     *  the value must be at least 0 too... */
                }

                this.pos = value;
            }
        }

        /// <summary>
        /// Writes a sequence of bytes to the buffer and advances the current position within the buffer by the number of bytes written.
        /// </summary>
        /// <param name="minibuf">An array of bytes. This method copies count bytes from minibuf to the buffer.</param>
        /// <param name="offset">The zero-based byte offset in minibuf at which to begin copying bytes to the buffer.</param>
        /// <param name="count">The number of bytes to be written to the buffer.</param>
        /// <exception cref="System.ObjectDisposedException">Thrown when the buffer is disposed.</exception>
        /// <exception cref="System.InvalidOperationException">Thrown when the buffer is in read mode or is finished.</exception>
        /// <exception cref="System.IO.EndOfStreamException">Thrown when the buffer cannot contain the given data.</exception>
        /// <exception cref="System.ArgumentNullException">Thrown when the given minibuf is null.</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">Thrown when the given offset is negative or beyond the length of minibuf -or- count is negative -or- the given count added to the offset goes beyond the length of minibuf.</exception>
        public override void Write(byte[] minibuf, int offset, int count)
        {
            _checkWrite();

            if (minibuf == null)
                throw new ArgumentNullException("minibuf");
            if (offset < 0 || offset > minibuf.Length)
                throw new ArgumentOutOfRangeException("offset", offset, "Offset cannot be negative or beyond the length of minibuf.");
            if (count < 0)
                throw new ArgumentOutOfRangeException("count", count, "The count cannot be negative.");
            if (offset + count > minibuf.Length)
                throw new ArgumentOutOfRangeException("count", count, "The count from the offset goes beyond the length of minibuf.");

            if (count > 0)
            {
                _ensureWrite(count);

                while (count > 0)
                {
                    int writeAmount = Math.Min((int)this.segCur.cnt - (int)this.segCurPos, count); //  As much as possible within the current segment.

                    System.Buffer.BlockCopy(minibuf, offset, this.segCur.arr, (int)(this.segCur.off + this.segCurPos), writeAmount);

                    offset += writeAmount;  //  Increase the offset within the mini-buffer.
                    count -= writeAmount;   //  Decrease the number of bytes left to write.
                    this.Position = this.pos + writeAmount;   //  Advance the position. This should also change the current segment and position within that segment.
                }
            }
        }

        /// <summary>
        /// Reads a sequence of bytes from the current buffer and advances the position within the buffer by the number of bytes read.
        /// </summary>
        /// <param name="minibuf">An array of bytes. When this method returns, the minibuf contains the specified byte array with the values between offset and (offset + count - 1) replaced by the bytes read from the buffer.</param>
        /// <param name="offset">The zero-based byte offset in minibuf at which to begin storing the data read from the buffer.</param>
        /// <param name="count">The maximum number of bytes to read from the buffer.</param>
        /// <returns>The total number of bytes read into the minibuf. This can be less than the number of bytes requested if that many bytes are not currently available, or zero (0) if the end of the buffer has been reached.</returns>
        /// <exception cref="System.ObjectDisposedException">Thrown when the buffer is disposed.</exception>
        /// <exception cref="System.InvalidOperationException">Thrown when the buffer is in write mode.</exception>
        /// <exception cref="System.ArgumentNullException">Thrown when the given minibuf is null.</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">Thrown when the given offset is negative or beyond the length of minibuf -or- count is negative -or- the given count added to the offset goes beyond the length of minibuf.</exception>
        public override int Read(byte[] minibuf, int offset, int count)
        {
            _checkRead();

            if (minibuf == null)
                throw new ArgumentNullException("minibuf");
            if (offset < 0 || offset > minibuf.Length)
                throw new ArgumentOutOfRangeException("offset", offset, "Offset cannot be negative or beyond the length of minibuf.");
            if (count < 0)
                throw new ArgumentOutOfRangeException("count", count, "The count cannot be negative.");
            if (offset + count > minibuf.Length)
                throw new ArgumentOutOfRangeException("count", count, "The count from the offset goes beyond the length of minibuf.");

            count = min(count, this.len - this.pos, minibuf.Length - offset); //  This is exactly how much of the buffer will be read.
            int res = count;  //  Copied so 'red' can be modified.

            while (count > 0)
            {
                int readAmount = Math.Min((int)this.segCur.cnt - (int)this.segCurPos, count); //  As much as possible within the current segment.

                System.Buffer.BlockCopy(this.segCur.arr, (int)(this.segCur.off + this.segCurPos), minibuf, offset, readAmount);

                offset += readAmount;  //  Increase the offset within the mini-buffer.
                count -= readAmount;   //  Decrease the number of bytes left to read.
                this.Position = this.pos + readAmount;   //  Advance the position. This should also change the current segment and position within that segment.
            }

            return res;
        }

        /// <summary>
        /// Sets the position in the buffer.
        /// </summary>
        /// <param name="offset">A byte offset relative to the origin parameter.</param>
        /// <param name="origin">A value of type <see cref="System.IO.SeekOrigin"/> indicating the reference point used to obtain the current position.</param>
        /// <returns>The new position in the stream.</returns>
        /// <exception cref="System.ObjectDisposedException">Thrown when the buffer is disposed.</exception>
        public override long Seek(long offset, SeekOrigin origin)
        {
            if (this._disposalStatus == -1)
                throw new ObjectDisposedException(this.GetType().FullName);

            switch (origin)
            {
                case SeekOrigin.Begin:
                    if (offset < 0)
                        throw new ArgumentOutOfRangeException("Offset points before the start of the buffer.");
                    if (offset > this.len)
                        throw new ArgumentOutOfRangeException("Offset points beyond the end of the buffer.");

                    return this.Position = offset;

                case SeekOrigin.End:
                    if (offset > 0)
                        throw new ArgumentOutOfRangeException("Offset points beyond the end of the buffer.");
                    if (offset < -this.len)
                        throw new ArgumentOutOfRangeException("Offset points before the start of the buffer.");

                    return this.Position = this.len + offset;

                case SeekOrigin.Current:
                    if (offset < -this.pos)
                        throw new ArgumentOutOfRangeException("Offset points before the start of the buffer.");
                    if (offset > this.len - this.pos)
                        throw new ArgumentOutOfRangeException("Offset points beyond the end of the buffer.");

                    return this.Position = this.pos + offset;
            }

            throw new ArgumentOutOfRangeException("origin", "Origin is not of a known value.");
        }

        /// <summary>
        /// Sets the length of the buffer.
        /// </summary>
        /// <param name="value">The desired length of the current buffer in bytes.</param>
        /// <exception cref="System.ObjectDisposedException">Thrown when the buffer is disposed.</exception>
        /// <exception cref="System.InvalidOperationException">Thrown when the buffer is in read mode or is finished.</exception>
        /// <exception cref="System.IO.EndOfStreamException">Thrown when the buffer cannot contain the given .</exception>
        public override void SetLength(long value)
        {
            _checkWrite();

            _ensureWrite(value - this.len); //  Handles all cases well (value < current length, > and =)
        }

        #endregion

        #region Utilitary Methods

        private long _ensureWrite(long extra, bool tolerant = false)
        {
            if (this.pos + extra > this.cap)
            {
                if ((mode & SegmentBufferModes.AutoEnlarge) == 0)
                    if (tolerant)
                        return (this.len = this.cap) - this.pos;    //  The length becomes the capacity.
                    else
                        throw new EndOfStreamException("Buffer is not large enough to contain the given data and auto-enlarging is disallowed.");

                long segCnt = 0L;
                if (Math.DivRem(this.pos + extra - this.cap, this.newSegLength, out segCnt) != 0L) segCnt++; //  If the remained of the division is not 0, one more segment is needed.
                this.cap += segCnt * this.newSegLength;

                for (; segCnt > 0L; segCnt--) this.segs.Add(new ByteArraySegment(new byte[this.newSegLength], 0, this.newSegLength, false)); //  Create all necessary segments.
            }

            this.len = this.pos + extra;
            //  It fits, so the length is increased here.

            return extra;
        }

        private void _ensureRead(long extra)
        {
            if (this.pos + extra > this.len)
            {
                throw new EndOfStreamException("Buffer is not large enough to contain the given data.");
            }
        }

        private void _checkWrite()
        {
            if (this._disposalStatus == -1)
                throw new ObjectDisposedException(this.GetType().FullName);
            if (this._disposalStatus == 1)
                throw new InvalidOperationException("Buffer is finished.");
            if ((this.mode & SegmentBufferModes.Write) == 0)
                throw new InvalidOperationException("Buffer is in read mode.");
        }

        private void _checkRead()
        {
            if (this._disposalStatus == -1)
                throw new ObjectDisposedException(this.GetType().FullName);
            if ((this.mode & SegmentBufferModes.Read) == 0 && this._disposalStatus != 1)
                throw new InvalidOperationException("Buffer is in write mode.");
        }

        private int min(int a, long b, int c)
        {
            int m = c < a ? c : a;
            if (b < m) m = (int)b;
            return m;
        }

        #endregion

        #region Stream Utilities

        /// <summary>
        /// Writes a sequence of bytes to the buffer and advances the current position within the buffer by the number of bytes written.
        /// </summary>
        /// <param name="minibuf">An array of bytes. This method copies all bytes from minibuf to the buffer.</param>
        /// <exception cref="System.ObjectDisposedException">Thrown when the buffer is disposed.</exception>
        /// <exception cref="System.InvalidOperationException">Thrown when the buffer is in read mode or is finished.</exception>
        /// <exception cref="System.IO.EndOfStreamException">Thrown when the buffer cannot contain the given data.</exception>
        /// <exception cref="System.ArgumentNullException">Thrown when the given minibuf is null.</exception>
        public void Write(byte[] minibuf)
        {
            Write(minibuf, 0, (minibuf ?? EmptyByteArray).Length);
        }

        /// <summary>
        /// Reads a sequence of bytes from the current buffer and advances the position within the buffer by the number of bytes read.
        /// </summary>
        /// <param name="minibuf">An array of bytes. When this method returns, the minibuf contains the specified byte array with the values replaced by the bytes read from the buffer.</param>
        /// <returns>The total number of bytes read into the minibuf. This can be less than the length of minibuf if that many bytes are not currently available, or zero (0) if the end of the buffer has been reached.</returns>
        /// <exception cref="System.ObjectDisposedException">Thrown when the buffer is disposed.</exception>
        /// <exception cref="System.InvalidOperationException">Thrown when the buffer is in write mode.</exception>
        /// <exception cref="System.ArgumentNullException">Thrown when the given minibuf is null.</exception>
        public int Read(byte[] minibuf)
        {
            return Read(minibuf, 0, (minibuf ?? EmptyByteArray).Length);
        }

        /// <summary>
        /// Writes a sequence of bytes to the buffer and advances the current position within the buffer by the number of bytes written.
        /// </summary>
        /// <param name="minibuf">A segment of an array of bytes. This method copies all bytes from minibuf to the buffer.</param>
        /// <exception cref="System.ObjectDisposedException">Thrown when the buffer is disposed.</exception>
        /// <exception cref="System.InvalidOperationException">Thrown when the buffer is in read mode or is finished.</exception>
        /// <exception cref="System.IO.EndOfStreamException">Thrown when the buffer cannot contain the given data.</exception>
        public void Write(ArraySegment<byte> minibuf)
        {
            Write(minibuf.Array, minibuf.Offset, minibuf.Count);
        }

        /// <summary>
        /// Reads a sequence of bytes from the current buffer and advances the position within the buffer by the number of bytes read.
        /// </summary>
        /// <param name="minibuf">A segment of an array of bytes. When this method returns, the minibuf contains the specified byte array with the values within the segment replaced by the bytes read from the buffer.</param>
        /// <returns>The total number of bytes read into the minibuf. This can be less than the segment count of minibuf if that many bytes are not currently available, or zero (0) if the end of the buffer has been reached.</returns>
        /// <exception cref="System.ObjectDisposedException">Thrown when the buffer is disposed.</exception>
        /// <exception cref="System.InvalidOperationException">Thrown when the buffer is in write mode.</exception>
        public int Read(ArraySegment<byte> minibuf)
        {
            return Read(minibuf.Array, minibuf.Offset, minibuf.Count);
        }

        /// <summary>
        /// Writes a byte to the current position in the buffer and advances the position within the buffer by one byte.
        /// </summary>
        /// <param name="value">The byte to write to the stream.</param>
        /// <exception cref="System.ObjectDisposedException">Thrown when the buffer is disposed.</exception>
        /// <exception cref="System.InvalidOperationException">Thrown when the buffer is in read mode or is finished.</exception>
        /// <exception cref="System.IO.EndOfStreamException">Thrown when the buffer cannot contain another byte.</exception>
        public override void WriteByte(byte value)
        {
            _checkWrite();
            _ensureWrite(1L);

            this.segCur.arr[this.segCur.off + this.segCurPos] = value;
            this.Position = this.pos + 1;   //  Advance position 1 byte.
        }

        /// <summary>
        /// Reads a byte from the buffer and advances the position within the buffer by one byte, or returns -1 if at the end of the buffer.
        /// </summary>
        /// <returns>The unsigned byte cast to a <see cref="System.Int32"/>, or -1 if at the end of the buffer.</returns>
        /// <exception cref="System.ObjectDisposedException">Thrown when the buffer is disposed.</exception>
        /// <exception cref="System.InvalidOperationException">Thrown when the buffer is in write mode.</exception>
        public override int ReadByte()
        {
            _checkRead();

            if (this.pos >= this.len)
                return -1;

            int val = this.segCur.arr[this.segCur.off + this.segCurPos];
            this.Position = this.pos + 1;   //  Advance position 1 byte.

            return val;
        }

        /// <summary>
        /// Writes <paramref name="count"/> bytes to the current buffer starting at the current position from the <paramref name="source"/> stream, advancing the position in the current buffer accordingly.
        /// </summary>
        /// <remarks>
        /// This is prefered over <see cref="System.IO.Stream.CopyTo(System.IO.Stream)"/> and <see cref="System.IO.Stream.CopyTo(System.IO.Stream, int)"/> because it uses the internal segments as buffers.
        /// </remarks>
        /// <param name="source">Stream from which to read data to write into the buffer.</param>
        /// <param name="count">Maximum amount of bytes to write.</param>
        /// <returns>The amount of bytes written.</returns>
        /// <exception cref="System.ObjectDisposedException">Thrown when the buffer is disposed.</exception>
        /// <exception cref="System.InvalidOperationException">Thrown when the buffer is in read mode or is finished.</exception>
        /// <exception cref="System.IO.EndOfStreamException">Thrown when the buffer cannot contain the given data.</exception>
        /// <exception cref="System.ArgumentNullException">Thrown when the given minibuf is null.</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">Thrown when the given offset is negative or beyond the length of minibuf -or- count is negative -or- the given count added to the offset goes beyond the length of minibuf.</exception>
        public long WriteFrom(Stream source, long count)
        {
            _checkWrite();

            if (source == null)
                throw new ArgumentNullException("source");
            if (!source.CanRead)
                throw new NotSupportedException("Source stream does not support writing.");
            if (count < 0)
                throw new ArgumentOutOfRangeException("count", count, "Number of bytes to write must be positive.");

            long res = count;

            while (count > 0L)
            {
                int writeAmount = (int)Math.Min(this.segCur.cnt - this.segCurPos, Math.Min(count, (long)int.MaxValue)); //  As much as possible within the current segment.
                writeAmount = (int)_ensureWrite(writeAmount, true); //  Tolerant writing - this will return the number of bytes that CAN be written into the buffer with current settings.

                int writtenAmount = source.Read(this.segCur.arr, (int)(this.segCur.off + this.segCurPos), writeAmount); //  Write into the buffer as much as the source stream allows.
                this.len += writtenAmount - writeAmount;

                count -= writtenAmount;   //  Decrease the number of bytes left to write.
                this.Position = this.pos + writtenAmount;   //  Advance the position. This should also change the current segment and position within that segment.

                if (writtenAmount < 1) return res - count; //  Nothing was written, so the end of the source stream must've been reached!
            }

            return res;
        }

        /// <summary>
        /// Reads <paramref name="count"/> bytes from the current buffer starting at the current position into the <paramref name="target"/> stream, advancing the position in the current buffer accordingly.
        /// </summary>
        /// <remarks>
        /// This is prefered over <see cref="System.IO.Stream.CopyTo(System.IO.Stream)"/> and <see cref="System.IO.Stream.CopyTo(System.IO.Stream, int)"/> because it uses the internal segments as buffers.
        /// </remarks>
        /// <param name="target">Stream into which to write the data read from the buffer.</param>
        /// <param name="count">Amount of bytes to read.</param>
        /// <exception cref="System.ObjectDisposedException">Thrown when the buffer is disposed.</exception>
        /// <exception cref="System.InvalidOperationException">Thrown when the buffer is in write mode.</exception>
        /// <exception cref="System.IO.EndOfStreamException">Thrown when the buffer does not contain the given <paramref name="count"/> of data.</exception>
        /// <exception cref="System.ArgumentNullException">Thrown when the given <paramref name="target"/> stream is null.</exception>
        /// <exception cref="System.NotSupportedException">Thrown when the <paramref name="target"/> stream cannot be written to.</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">Thrown when the given <paramref name="count"/> is negative.</exception>
        public void ReadInto(Stream target, long count)
        {
            _checkRead();

            if (target == null)
                throw new ArgumentNullException("target");
            if (!target.CanWrite)
                throw new NotSupportedException("Target stream does not support writing.");
            if (count < 0)
                throw new ArgumentOutOfRangeException("count", count, "Number of bytes to read must be positive.");

            _ensureRead(count);

            while (count > 0L)
            {
                int readAmount = (int)Math.Min(this.segCur.cnt - this.segCurPos, Math.Min(count, (long)int.MaxValue)); //  As much as possible within the current segment.

                target.Write(this.segCur.arr, (int)(this.segCur.off + this.segCurPos), readAmount); //  Why the douche are the arguments 32-bit integers and not 64-bit?

                count -= readAmount;   //  Decrease the number of bytes left to read.
                this.Position = this.pos + readAmount;   //  Advance the position. This should also change the current segment and position within that segment.
            }
        }

#if SEGMENTBUFFER_USEASYNC  //  You may want to add this conditional compilation symbol to your project if your target .NET version has WriteAsync and ReadAsync in System.IO.Stream

        /// <summary>
        /// Asynchronously writes <paramref name="count"/> bytes to the current buffer starting at the current position from the <paramref name="source"/> stream, advancing the position in the current buffer accordingly.
        /// </summary>
        /// <remarks>
        /// This is prefered over <see cref="System.IO.Stream.CopyTo(System.IO.Stream)"/> and <see cref="System.IO.Stream.CopyTo(System.IO.Stream, int)"/> because it uses the internal segments as buffers.
        /// </remarks>
        /// <param name="source">Stream from which to read data to write into the buffer.</param>
        /// <param name="count">Maximum amount of bytes to write.</param>
        /// <returns>The amount of bytes written.</returns>
        /// <exception cref="System.ObjectDisposedException">Thrown when the buffer is disposed.</exception>
        /// <exception cref="System.InvalidOperationException">Thrown when the buffer is in read mode or is finished.</exception>
        /// <exception cref="System.IO.EndOfStreamException">Thrown when the buffer cannot contain the given data.</exception>
        /// <exception cref="System.ArgumentNullException">Thrown when the given minibuf is null.</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">Thrown when the given offset is negative or beyond the length of minibuf -or- count is negative -or- the given count added to the offset goes beyond the length of minibuf.</exception>
        public async Task<long> WriteFromAsync(Stream source, long count)
        {
            _checkWrite();

            if (source == null)
                throw new ArgumentNullException("source");
            if (!source.CanRead)
                throw new NotSupportedException("Source stream does not support writing.");
            if (count < 0)
                throw new ArgumentOutOfRangeException("count", count, "Number of bytes to write must be positive.");

            long res = count;

            while (count > 0L)
            {
                int writeAmount = (int)Math.Min(this.segCur.cnt - this.segCurPos, Math.Min(count, (long)int.MaxValue)); //  As much as possible within the current segment.
                writeAmount = (int)_ensureWrite(writeAmount, true); //  Tolerant writing - this will return the number of bytes that CAN be written into the buffer with current settings.

                int writtenAmount = await source.ReadAsync(this.segCur.arr, (int)(this.segCur.off + this.segCurPos), writeAmount); //  Write into the buffer as much as the source stream allows.
                this.len += writtenAmount - writeAmount;

                count -= writtenAmount;   //  Decrease the number of bytes left to write.
                this.Position = this.pos + writtenAmount;   //  Advance the position. This should also change the current segment and position within that segment.

                if (writtenAmount < 1) return res - count; //  Nothing was written, so the end of the source stream must've been reached!
            }

            return res;
        }

        /// <summary>
        /// Asynchronously reads <paramref name="count"/> bytes from the current buffer starting at the current position into the <paramref name="target"/> stream, advancing the position in the current buffer accordingly.
        /// </summary>
        /// <remarks>
        /// This is prefered over <see cref="System.IO.Stream.CopyTo(System.IO.Stream)"/> and <see cref="System.IO.Stream.CopyTo(System.IO.Stream, int)"/> because it uses the internal segments as buffers.
        /// </remarks>
        /// <param name="target">Stream into which to write the data read from the buffer.</param>
        /// <param name="count">Amount of bytes to read.</param>
        /// <exception cref="System.ObjectDisposedException">Thrown when the buffer is disposed.</exception>
        /// <exception cref="System.InvalidOperationException">Thrown when the buffer is in write mode.</exception>
        /// <exception cref="System.IO.EndOfStreamException">Thrown when the buffer does not contain the given <paramref name="count"/> of data.</exception>
        /// <exception cref="System.ArgumentNullException">Thrown when the given <paramref name="target"/> stream is null.</exception>
        /// <exception cref="System.NotSupportedException">Thrown when the <paramref name="target"/> stream cannot be written to.</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">Thrown when the given <paramref name="count"/> is negative.</exception>
        public async Task ReadIntoAsync(Stream target, long count)
        {
            _checkRead();

            if (target == null)
                throw new ArgumentNullException("target");
            if (!target.CanWrite)
                throw new NotSupportedException("Target stream does not support writing.");
            if (count < 0)
                throw new ArgumentOutOfRangeException("count", count, "Number of bytes to read must be positive.");

            _ensureRead(count);

            while (count > 0L)
            {
                int readAmount = (int)Math.Min(this.segCur.cnt - this.segCurPos, Math.Min(count, (long)int.MaxValue)); //  As much as possible within the current segment.

                await target.WriteAsync(this.segCur.arr, (int)(this.segCur.off + this.segCurPos), readAmount); //  Why the douche are the arguments 32-bit integers and not 64-bit?

                count -= readAmount;   //  Decrease the number of bytes left to read.
                this.Position = this.pos + readAmount;   //  Advance the position. This should also change the current segment and position within that segment.
            }
        }

#endif

        #endregion

#if SEGMENTATION_DEBUG
#pragma warning disable 1591    //  Ya' need no documentation for these.
        public int SegmentIndex { get { return this.segCurIndex; } }
        public long SegmentCurrentProgress { get { return this.segCurProg; } }
        public long SegmetnCurrentPosition { get { return this.segCurPos; } }
#pragma warning restore 1591

        //  These properties were used in unit tests to access the internal state of the segment buffer.
        //  If, for some reason, you need them, just add the above definition (SEGMENTATION_DEBUG) to
        //  the conditional compilation symbols under the 'build' tab of your project's settings.
#endif
    }

    class ByteArraySegment
    {
        /* The SegmentBuffer uses this class to store segments of byte arrays internally.
         * It counts on this class being immutable. More specifically, the 'cnt' field.
         * I highly recommend not digging your way into the class(es) with reflection
         * and changing stuff around. */

        internal byte[] arr;
        internal long off, cnt;
        internal bool external;

        internal ByteArraySegment(byte[] arr, long off, long cnt, bool external)
        {
            this.arr = arr;
            this.off = off;
            this.cnt = cnt;
            this.external = external;
        }

        public static implicit operator ByteArraySegment(ArraySegment<byte> b)
        {
            return new ByteArraySegment(b.Array, b.Offset, b.Count, true);
        }

        public static ByteArraySegment Copy(ArraySegment<byte> seg, bool external = false)
        {
            byte[] arr = new byte[seg.Count];

            System.Buffer.BlockCopy(seg.Array, seg.Offset, arr, 0, seg.Count);

            return new ByteArraySegment(arr, 0, arr.LongLength, external);
        }

        public static IEnumerable<ByteArraySegment> Copy(IEnumerable<ArraySegment<byte>> segs, bool external = false)
        {
            var sgar = segs.ToArray();  // It is much easier to work with the segments this way.
            var sgarind = 0;

            long length = 0L;    //  Total number of bytes in the given segments is computed.
            for (int i = 0; i < sgar.Length; i++) length += (long)sgar[i].Count;

            while (length > 0L)
            {
                byte[] arr = null;
                long lastLength = length;
                int sgarTempInd = sgar.Length - 1;

                while (arr == null && lastLength > 0 && sgarTempInd >= sgarind)
                    try { arr = new byte[lastLength]; }
                    catch { lastLength -= sgar[sgarTempInd--].Count; }
                //  Try to create an array as big as possible to accomodate as many segments as possible.

                if (arr == null || arr.Length == 0)
                    throw new Exception("This just should not happen! Could not create an array for copying an enumeration of segments.");

                lastLength = 0L; //  Repurposing variable for the offset within the new array!
                for (; sgarind <= sgarTempInd; sgarind++)
                {
                    System.Buffer.BlockCopy(sgar[sgarind].Array, sgar[sgarind].Offset, arr, (int)lastLength, sgar[sgarind].Count);
                    lastLength += sgar[sgarind].Count;
                }

                yield return new ByteArraySegment(arr, 0L, lastLength, external);    //  Using 'lastLength' just in case subtraction goes south.
                length -= lastLength;   //                                              (or my math skills)
            }
        }
    }

    /// <summary>
    /// Possible modes of operation for <see cref="System.IO.SegmentBuffer"/>s.
    /// </summary>
    [Flags]
    public enum SegmentBufferModes
    {
        /// <summary>
        /// Data will only be read from the buffer.
        /// </summary>
        Read = 0x0001,
        /// <summary>
        /// Data will only be written to the buffer.
        /// </summary>
        Write = 0x0002,

        /// <summary>
        /// Buffer will be automatically enlarged for writing operations.
        /// </summary>
        AutoEnlarge = 0x0010,
        /// <summary>
        /// Preserves the initial data given to the buffer for writing (does not make a copy).
        /// </summary>
        PreserveInitialData = 0x0020,
    }
}
