﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.Diagnostics.Runtime
{
    /// <summary>
    /// Returns information about the IL for a method.
    /// </summary>
    public class ILInfo
    {
        /// <summary>
        /// The address in memory of where the IL for a particular method is located.
        /// </summary>
        public ulong Address { get; }

        /// <summary>
        /// The length (in bytes) of the IL method body.
        /// </summary>
        public int Length { get; }

        /// <summary>
        /// The flags associated with the IL code.
        /// </summary>
        public uint Flags { get; }

        /// <summary>
        /// The local variable signature token for this IL method.
        /// </summary>
        public uint LocalVarSignatureToken { get; }

        public ILInfo(ulong address, int len, uint flags, uint localVarSignatureToken)
        {
            Address = address;
            Length = len;
            Flags = flags;
            LocalVarSignatureToken = localVarSignatureToken;
        }
    }
}
