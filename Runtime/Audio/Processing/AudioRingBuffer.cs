using System;
using System.Threading;

namespace Tsc.AIBridge.Audio.Processing
{
    /// <summary>
    /// Thread-safe ring buffer for audio streaming.
    /// Handles jitter compensation and prevents audio dropouts.
    /// </summary>
    public class AudioRingBuffer
    {
        private readonly byte[] buffer;
        private readonly int capacity;
        private readonly object lockObject = new object();

        private int writePosition;
        private int readPosition;
        private int availableBytes;

        // Statistics
        private long totalBytesWritten;
        private long totalBytesRead;
        private int overrunCount;
        private int underrunCount;

        public AudioRingBuffer(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentException("Capacity must be positive", nameof(capacity));

            this.capacity = capacity;
            buffer = new byte[capacity];
        }

        /// <summary>
        /// Write data to the buffer
        /// </summary>
        public int Write(byte[] data)
        {
            if (data == null || data.Length == 0)
                return 0;

            return Write(data, 0, data.Length);
        }

        /// <summary>
        /// Write data to the buffer with offset and count
        /// </summary>
        public int Write(byte[] data, int offset, int count)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (offset < 0 || offset >= data.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0 || offset + count > data.Length)
                throw new ArgumentOutOfRangeException(nameof(count));

            lock (lockObject)
            {
                // Check available space
                var freeSpace = capacity - availableBytes;
                if (count > freeSpace)
                {
                    // Buffer overrun - we'll write what we can
                    overrunCount++;
                    count = freeSpace;

                    if (count == 0)
                        return 0; // Buffer completely full
                }

                // Write data in up to two chunks (before and after wrap)
                var bytesWritten = 0;

                while (bytesWritten < count)
                {
                    var bytesToWrite = Math.Min(count - bytesWritten, capacity - writePosition);
                    Array.Copy(data, offset + bytesWritten, buffer, writePosition, bytesToWrite);

                    writePosition = (writePosition + bytesToWrite) % capacity;
                    bytesWritten += bytesToWrite;
                }

                availableBytes += bytesWritten;
                totalBytesWritten += bytesWritten;

                return bytesWritten;
            }
        }

        /// <summary>
        /// Read data from the buffer
        /// </summary>
        public int Read(byte[] data, int offset, int count)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (offset < 0 || offset >= data.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0 || offset + count > data.Length)
                throw new ArgumentOutOfRangeException(nameof(count));

            lock (lockObject)
            {
                // Check available data
                if (availableBytes == 0)
                {
                    underrunCount++;
                    return 0;
                }

                // Read only what's available
                count = Math.Min(count, availableBytes);

                // Read data in up to two chunks (before and after wrap)
                var bytesRead = 0;

                while (bytesRead < count)
                {
                    var bytesToRead = Math.Min(count - bytesRead, capacity - readPosition);
                    Array.Copy(buffer, readPosition, data, offset + bytesRead, bytesToRead);

                    readPosition = (readPosition + bytesToRead) % capacity;
                    bytesRead += bytesToRead;
                }

                availableBytes -= bytesRead;
                totalBytesRead += bytesRead;

                return bytesRead;
            }
        }

        /// <summary>
        /// Peek at data without removing it from the buffer
        /// </summary>
        public int Peek(byte[] data, int offset, int count)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            lock (lockObject)
            {
                var savedReadPosition = readPosition;
                var savedAvailableBytes = availableBytes;

                var bytesRead = Read(data, offset, count);

                // Restore state
                readPosition = savedReadPosition;
                availableBytes = savedAvailableBytes;

                return bytesRead;
            }
        }

        /// <summary>
        /// Clear all data from the buffer
        /// </summary>
        public void Clear()
        {
            lock (lockObject)
            {
                writePosition = 0;
                readPosition = 0;
                availableBytes = 0;
                Array.Clear(buffer, 0, capacity);
            }
        }

        /// <summary>
        /// Skip bytes in the buffer without reading them
        /// </summary>
        public int Skip(int count)
        {
            lock (lockObject)
            {
                count = Math.Min(count, availableBytes);
                readPosition = (readPosition + count) % capacity;
                availableBytes -= count;
                return count;
            }
        }

        // Properties
        public int AvailableBytes
        {
            get
            {
                lock (lockObject)
                {
                    return availableBytes;
                }
            }
        }

        public int FreeSpace
        {
            get
            {
                lock (lockObject)
                {
                    return capacity - availableBytes;
                }
            }
        }

        public int Capacity => capacity;

        public float FillPercentage
        {
            get
            {
                lock (lockObject)
                {
                    return availableBytes / (float)capacity;
                }
            }
        }

        // Statistics
        public long TotalBytesWritten => Interlocked.Read(ref totalBytesWritten);
        public long TotalBytesRead => Interlocked.Read(ref totalBytesRead);
        public int OverrunCount => overrunCount;
        public int UnderrunCount => underrunCount;

        public void ResetStatistics()
        {
            Interlocked.Exchange(ref totalBytesWritten, 0);
            Interlocked.Exchange(ref totalBytesRead, 0);
            Interlocked.Exchange(ref overrunCount, 0);
            Interlocked.Exchange(ref underrunCount, 0);
        }
    }
}