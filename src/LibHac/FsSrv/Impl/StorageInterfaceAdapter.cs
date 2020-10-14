﻿using System;
using System.Runtime.CompilerServices;
using LibHac.Common;
using LibHac.Fs;
using LibHac.FsSrv.Sf;

namespace LibHac.FsSrv.Impl
{
    internal class StorageInterfaceAdapter : IStorageSf
    {
        private ReferenceCountedDisposable<IStorage> BaseStorage { get; }

        public StorageInterfaceAdapter(ReferenceCountedDisposable<IStorage> baseStorage)
        {
            BaseStorage = baseStorage.AddReference();
        }

        public Result Read(long offset, Span<byte> destination)
        {
            const int maxTryCount = 2;

            if (offset < 0)
                return ResultFs.InvalidOffset.Log();

            if (destination.Length < 0)
                return ResultFs.InvalidSize.Log();

            Result rc = Result.Success;

            for (int tryNum = 0; tryNum < maxTryCount; tryNum++)
            {
                rc = BaseStorage.Target.Read(offset, destination);

                // Retry on ResultDataCorrupted
                if (!ResultFs.DataCorrupted.Includes(rc))
                    break;
            }

            return rc;
        }

        public Result Write(long offset, ReadOnlySpan<byte> source)
        {
            if (offset < 0)
                return ResultFs.InvalidOffset.Log();

            if (source.Length < 0)
                return ResultFs.InvalidSize.Log();

            // Note: Thread priority is temporarily when writing in FS

            return BaseStorage.Target.Write(offset, source);
        }

        public Result Flush()
        {
            return BaseStorage.Target.Flush();
        }

        public Result SetSize(long size)
        {
            if (size < 0)
                return ResultFs.InvalidSize.Log();

            return BaseStorage.Target.SetSize(size);
        }

        public Result GetSize(out long size)
        {
            return BaseStorage.Target.GetSize(out size);
        }

        public Result OperateRange(out QueryRangeInfo rangeInfo, int operationId, long offset, long size)
        {
            rangeInfo = new QueryRangeInfo();

            if (operationId == (int)OperationId.InvalidateCache)
            {
                Result rc = BaseStorage.Target.OperateRange(Span<byte>.Empty, OperationId.InvalidateCache, offset, size,
                    ReadOnlySpan<byte>.Empty);
                if (rc.IsFailure()) return rc;
            }
            else if (operationId == (int)OperationId.QueryRange)
            {
                Unsafe.SkipInit(out QueryRangeInfo info);

                Result rc = BaseStorage.Target.OperateRange(SpanHelpers.AsByteSpan(ref info), OperationId.QueryRange,
                    offset, size, ReadOnlySpan<byte>.Empty);
                if (rc.IsFailure()) return rc;

                rangeInfo.Merge(in info);
            }

            return Result.Success;
        }
        public void Dispose()
        {
            BaseStorage?.Dispose();
        }
    }
}
