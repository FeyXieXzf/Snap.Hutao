﻿// Copyright (c) DGP Studio. All rights reserved.
// Licensed under the MIT license.

using System.Runtime.CompilerServices;
using WinRT;

namespace Snap.Hutao.Extension;

internal static class WinRTExtension
{
    public static bool IsDisposed(this IWinRTObject obj)
    {
        IObjectReference objectReference = obj.NativeObject;

        lock (GetDisposedLock(objectReference))
        {
            return GetDisposed(objectReference);
        }
    }

    // protected bool disposed;
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name ="disposed")]
    private static extern ref bool GetDisposed(IObjectReference objRef);

    // private object _disposedLock
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_disposedLock")]
    private static extern ref object GetDisposedLock(IObjectReference objRef);
}