﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Linq;
using Xunit;

namespace Microsoft.Diagnostics.Runtime.Tests
{
    public class FieldTests
    {
        [Fact]
        public void InstanceFieldProperties()
        {
            using DataTarget dt = TestTargets.Types.LoadFullDump();
            using ClrRuntime runtime = dt.ClrVersions.Single().CreateRuntime();
            ClrHeap heap = runtime.Heap;

            ClrType foo = runtime.GetModule("sharedlibrary.dll").GetTypeByName("Foo");
            Assert.NotNull(foo);

            CheckField(foo, "i", ClrElementType.Int32, "System.Int32", 4);


            CheckField(foo, "s", ClrElementType.String, "System.String", IntPtr.Size);
            CheckField(foo, "b", ClrElementType.Boolean, "System.Boolean", 1);
            CheckField(foo, "f", ClrElementType.Float, "System.Single", 4);
            CheckField(foo, "d", ClrElementType.Double, "System.Double", 8);
            CheckField(foo, "o", ClrElementType.Object, "System.Object", IntPtr.Size);
        }

        private static void CheckField(ClrType type, string fieldName, ClrElementType element, string typeName, int size)
        {
            ClrInstanceField field = type.GetFieldByName(fieldName);
            Assert.NotNull(field);
            Assert.NotNull(field.Type);

            Assert.Equal(element, field.ElementType);
            Assert.Equal(typeName, field.Type.Name);
            Assert.Equal(size, field.Size);
        }
    }
}
