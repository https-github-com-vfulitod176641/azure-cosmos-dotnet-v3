﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.CosmosElements
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Microsoft.Azure.Cosmos.Json;
    using Microsoft.Azure.Cosmos.Query.Core.Monads;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public class CosmosElementDeserializerTests
    {
        [TestMethod]
        public void ArrayTest()
        {
            {
                // Schemaless array
                IJsonWriter jsonWriter = JsonWriter.Create(JsonSerializationFormat.Binary);
                object[] arrayValue = new object[] { (Number64)1, (Number64)2, (Number64)3 };
                jsonWriter.WriteArrayStart();
                jsonWriter.WriteNumberValue(1);
                jsonWriter.WriteNumberValue(2);
                jsonWriter.WriteNumberValue(3);
                jsonWriter.WriteArrayEnd();
                ReadOnlyMemory<byte> buffer = jsonWriter.GetResult();

                {
                    // positive
                    TryCatch<IReadOnlyList<object>> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<IReadOnlyList<object>>(buffer);
                    Assert.IsTrue(tryDeserialize.Succeeded);
                    Assert.IsTrue(tryDeserialize.Result.SequenceEqual(arrayValue));
                }

                {
                    // negative
                    TryCatch<int> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<int>(buffer);
                    Assert.IsFalse(tryDeserialize.Succeeded);
                }
            }

            {
                // Array with schema
                IJsonWriter jsonWriter = JsonWriter.Create(JsonSerializationFormat.Binary);
                Person[] arrayValue = new Person[] { new Person("John", 24) };
                jsonWriter.WriteArrayStart();
                jsonWriter.WriteObjectStart();
                jsonWriter.WriteFieldName("name");
                jsonWriter.WriteStringValue("John");
                jsonWriter.WriteFieldName("age");
                jsonWriter.WriteNumberValue(24);
                jsonWriter.WriteObjectEnd();
                jsonWriter.WriteArrayEnd();
                ReadOnlyMemory<byte> buffer = jsonWriter.GetResult();

                TryCatch<IReadOnlyList<Person>> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<IReadOnlyList<Person>>(buffer);
                Assert.IsTrue(tryDeserialize.Succeeded);
                Assert.IsTrue(tryDeserialize.Result.SequenceEqual(arrayValue));
            }
        }

        [TestMethod]
        public void BinaryTest()
        {
            IJsonWriter jsonWriter = JsonWriter.Create(JsonSerializationFormat.Binary);
            byte[] binaryValue = new byte[] { 1, 2, 3 };
            jsonWriter.WriteBinaryValue(binaryValue);
            ReadOnlyMemory<byte> buffer = jsonWriter.GetResult();

            {
                // positive
                TryCatch<ReadOnlyMemory<byte>> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<ReadOnlyMemory<byte>>(buffer);
                Assert.IsTrue(tryDeserialize.Succeeded);
                Assert.IsTrue(tryDeserialize.Result.ToArray().SequenceEqual(binaryValue));
            }

            {
                // negative
                TryCatch<int> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<int>(buffer);
                Assert.IsFalse(tryDeserialize.Succeeded);
            }
        }

        [TestMethod]
        public void BooleanTest()
        {
            IJsonWriter jsonWriter = JsonWriter.Create(JsonSerializationFormat.Binary);
            jsonWriter.WriteBoolValue(true);
            ReadOnlyMemory<byte> buffer = jsonWriter.GetResult();

            {
                // positive
                TryCatch<bool> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<bool>(buffer);
                Assert.IsTrue(tryDeserialize.Succeeded);
                Assert.AreEqual(true, tryDeserialize.Result);
            }

            {
                // negative
                TryCatch<int> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<int>(buffer);
                Assert.IsFalse(tryDeserialize.Succeeded);
            }
        }

        [TestMethod]
        public void GuidTest()
        {
            IJsonWriter jsonWriter = JsonWriter.Create(JsonSerializationFormat.Binary);
            jsonWriter.WriteGuidValue(Guid.Empty);
            ReadOnlyMemory<byte> buffer = jsonWriter.GetResult();

            {
                // positive
                TryCatch<Guid> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<Guid>(buffer);
                Assert.IsTrue(tryDeserialize.Succeeded);
                Assert.AreEqual(Guid.Empty, tryDeserialize.Result);
            }

            {
                // negative
                TryCatch<int> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<int>(buffer);
                Assert.IsFalse(tryDeserialize.Succeeded);
            }
        }

        [TestMethod]
        public void NullTest()
        {
            IJsonWriter jsonWriter = JsonWriter.Create(JsonSerializationFormat.Binary);
            jsonWriter.WriteNullValue();
            ReadOnlyMemory<byte> buffer = jsonWriter.GetResult();

            {
                // object
                TryCatch<CosmosClient> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<CosmosClient>(buffer);
                Assert.IsTrue(tryDeserialize.Succeeded);
                Assert.AreEqual(null, tryDeserialize.Result);
            }

            {
                // nullable
                TryCatch<int?> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<int?>(buffer);
                Assert.IsTrue(tryDeserialize.Succeeded);
                Assert.AreEqual(null, tryDeserialize.Result);
            }

            {
                // struct
                TryCatch<int> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<int>(buffer);
                Assert.IsFalse(tryDeserialize.Succeeded);
            }
        }

        [TestMethod]
        public void NumberTest()
        {
            int integerValue = 42;
            IJsonWriter integerWriter = JsonWriter.Create(JsonSerializationFormat.Binary);
            integerWriter.WriteNumberValue(integerValue);
            ReadOnlyMemory<byte> integerBuffer = integerWriter.GetResult();

            {
                TryCatch<sbyte> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<sbyte>(integerBuffer);
                Assert.IsTrue(tryDeserialize.Succeeded);
                Assert.AreEqual((long)integerValue, (long)tryDeserialize.Result);
            }

            {
                TryCatch<byte> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<byte>(integerBuffer);
                Assert.IsTrue(tryDeserialize.Succeeded);
                Assert.AreEqual((long)integerValue, (long)tryDeserialize.Result);
            }

            {
                TryCatch<short> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<short>(integerBuffer);
                Assert.IsTrue(tryDeserialize.Succeeded);
                Assert.AreEqual((long)integerValue, (long)tryDeserialize.Result);
            }

            {
                TryCatch<int> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<int>(integerBuffer);
                Assert.IsTrue(tryDeserialize.Succeeded);
                Assert.AreEqual((long)integerValue, (long)tryDeserialize.Result);
            }

            {
                TryCatch<long> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<long>(integerBuffer);
                Assert.IsTrue(tryDeserialize.Succeeded);
                Assert.AreEqual((long)integerValue, (long)tryDeserialize.Result);
            }

            {
                TryCatch<ushort> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<ushort>(integerBuffer);
                Assert.IsTrue(tryDeserialize.Succeeded);
                Assert.AreEqual((long)integerValue, (long)tryDeserialize.Result);
            }

            {
                TryCatch<uint> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<uint>(integerBuffer);
                Assert.IsTrue(tryDeserialize.Succeeded);
                Assert.AreEqual((long)integerValue, (long)tryDeserialize.Result);
            }

            {
                TryCatch<ulong> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<ulong>(integerBuffer);
                Assert.IsTrue(tryDeserialize.Succeeded);
                Assert.AreEqual((long)integerValue, (long)tryDeserialize.Result);
            }

            {
                TryCatch<decimal> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<decimal>(integerBuffer);
                Assert.IsTrue(tryDeserialize.Succeeded);
                Assert.AreEqual((long)integerValue, (long)tryDeserialize.Result);
            }

            {
                TryCatch<float> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<float>(integerBuffer);
                Assert.IsFalse(tryDeserialize.Succeeded);
            }

            {
                TryCatch<double> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<double>(integerBuffer);
                Assert.IsFalse(tryDeserialize.Succeeded);
            }

            float doubleValue = 42.1337f;
            IJsonWriter doubleWriter = JsonWriter.Create(JsonSerializationFormat.Binary);
            doubleWriter.WriteNumberValue(doubleValue);
            ReadOnlyMemory<byte> doubleBuffer = doubleWriter.GetResult();

            {
                TryCatch<sbyte> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<sbyte>(doubleBuffer);
                Assert.IsFalse(tryDeserialize.Succeeded);
            }

            {
                TryCatch<byte> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<byte>(doubleBuffer);
                Assert.IsFalse(tryDeserialize.Succeeded);
            }

            {
                TryCatch<short> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<short>(doubleBuffer);
                Assert.IsFalse(tryDeserialize.Succeeded);
            }

            {
                TryCatch<int> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<int>(doubleBuffer);
                Assert.IsFalse(tryDeserialize.Succeeded);
            }

            {
                TryCatch<long> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<long>(doubleBuffer);
                Assert.IsFalse(tryDeserialize.Succeeded);
            }

            {
                TryCatch<ushort> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<ushort>(doubleBuffer);
                Assert.IsFalse(tryDeserialize.Succeeded);
            }

            {
                TryCatch<uint> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<uint>(doubleBuffer);
                Assert.IsFalse(tryDeserialize.Succeeded);
            }

            {
                TryCatch<ulong> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<ulong>(doubleBuffer);
                Assert.IsFalse(tryDeserialize.Succeeded);
            }

            {
                TryCatch<decimal> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<decimal>(doubleBuffer);
                Assert.IsFalse(tryDeserialize.Succeeded);
            }

            {
                TryCatch<float> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<float>(doubleBuffer);
                Assert.IsTrue(tryDeserialize.Succeeded);
                Assert.AreEqual(doubleValue, tryDeserialize.Result);
            }

            {
                TryCatch<double> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<double>(doubleBuffer);
                Assert.IsTrue(tryDeserialize.Succeeded);
                Assert.AreEqual(doubleValue, tryDeserialize.Result);
            }
        }

        [TestMethod]
        public void ObjectTest()
        {
            IJsonWriter jsonWriter = JsonWriter.Create(JsonSerializationFormat.Binary);
            jsonWriter.WriteObjectStart();

            jsonWriter.WriteFieldName("name");
            jsonWriter.WriteStringValue("John");

            jsonWriter.WriteFieldName("age");
            jsonWriter.WriteNumberValue(24);

            jsonWriter.WriteObjectEnd();
            ReadOnlyMemory<byte> buffer = jsonWriter.GetResult();

            {
                // positive
                TryCatch<Person> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<Person>(buffer);
                Assert.IsTrue(tryDeserialize.Succeeded);
                Assert.AreEqual("John", tryDeserialize.Result.Name);
                Assert.AreEqual(24, tryDeserialize.Result.Age);
            }

            {
                // negative
                TryCatch<int> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<int>(buffer);
                Assert.IsFalse(tryDeserialize.Succeeded);
            }
        }

        [TestMethod]
        public void StringTest()
        {
            IJsonWriter jsonWriter = JsonWriter.Create(JsonSerializationFormat.Binary);
            jsonWriter.WriteStringValue("asdf");
            ReadOnlyMemory<byte> buffer = jsonWriter.GetResult();

            {
                // Positive
                TryCatch<string> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<string>(buffer);
                Assert.IsTrue(tryDeserialize.Succeeded);
                Assert.AreEqual("asdf", tryDeserialize.Result);
            }

            {
                // Negative
                TryCatch<int> tryDeserialize = CosmosElementDeserializer.Monadic.Deserialize<int>(buffer);
                Assert.IsFalse(tryDeserialize.Succeeded);
            }
        }

        private sealed class Person
        {
            public Person(string name, int age)
            {
                this.Name = name;
                this.Age = age;
            }

            public string Name { get; }
            public int Age { get; }

            public override bool Equals(object obj)
            {
                if (!(obj is Person person))
                {
                    return false;
                }

                return this.Equals(person);
            }

            public bool Equals(Person other)
            {
                return (this.Name == other.Name) && (this.Age == other.Age);
            }

            public override int GetHashCode()
            {
                return 0;
            }
        }
    }
}