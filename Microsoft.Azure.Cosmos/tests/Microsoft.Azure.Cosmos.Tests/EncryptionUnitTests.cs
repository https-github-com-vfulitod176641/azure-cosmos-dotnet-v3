﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.Fluent;
    using Microsoft.Azure.Cosmos.Tests;
    using Microsoft.Azure.Documents;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using Moq;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    [TestClass]
    public class EncryptionUnitTests
    {
        private const string DatabaseId = "mockDatabase";
        private const string ContainerId = "mockContainer";
        private const double requestCharge = 0.6;

        private TimeSpan cacheTTL = TimeSpan.FromDays(1);
        private byte[] dek = new byte[] { 1, 2, 3, 4 };
        private KeyWrapMetadata metadata1 = new KeyWrapMetadata("metadata1");
        private KeyWrapMetadata metadata2 = new KeyWrapMetadata("metadata2");
        private string metadataUpdateSuffix = "updated";

        private Mock<KeyWrapProvider> mockKeyWrapProvider;
        private Mock<EncryptionAlgorithm> mockEncryptionAlgorithm;
        private Mock<DatabaseCore> mockDatabaseCore;

        [TestMethod]
        public void EncryptionUTKeyWrapProviderNotUsedWithCustomSerializer()
        {
            CosmosClientOptions cosmosClientOptions = new CosmosClientOptions();
            cosmosClientOptions.Serializer = new Mock<CosmosSerializer>().Object;
            try
            {
                cosmosClientOptions.KeyWrapProvider = new Mock<KeyWrapProvider>().Object;
                Assert.Fail();
            }
            catch(ArgumentException ex)
            {
                Assert.AreEqual(ClientResources.CustomSerializerAndEncryptionNotSupportedTogether, ex.Message);
            }
        }

        [TestMethod]
        public void EncryptionUTKeyWrapProviderSetInOptions()
        {
            KeyWrapProvider keyWrapProvider = new Mock<KeyWrapProvider>().Object;
            CosmosClientBuilder cosmosClientBuilder = new CosmosClientBuilder("http://localhost", Guid.NewGuid().ToString()).WithKeyWrapProvider(keyWrapProvider);
            CosmosClient client = cosmosClientBuilder.Build(new MockDocumentClient());
            Assert.AreEqual(keyWrapProvider, client.ClientOptions.KeyWrapProvider);
        }

        [TestMethod]
        public async Task EncryptionUTWriteDekWithoutKeyWrapProvider()
        {
            Database database = ((ContainerCore)this.GetContainer()).Database;

            try
            {
                await database.CreateDataEncryptionKeyAsync("mydek", this.metadata1);
                Assert.Fail();
            }
            catch (ArgumentException ex)
            {
                Assert.AreEqual(ClientResources.KeyWrapProviderNotConfigured, ex.Message);
            }

            try
            {
                DataEncryptionKey dek = database.GetDataEncryptionKey("mydek");
                await dek.RewrapAsync(this.metadata2);
                Assert.Fail();
            }
            catch (ArgumentException ex)
            {
                Assert.AreEqual(ClientResources.KeyWrapProviderNotConfigured, ex.Message);
            }
        }

        [TestMethod]
        public async Task EncryptionUTCreateDek()
        {
            EncryptionTestHandler encryptionTestHandler = new EncryptionTestHandler();
            Container container = this.GetContainerWithMockSetup(encryptionTestHandler);
            Database database = ((ContainerCore)container).Database;

            string dekId = "mydek";
            DataEncryptionKeyResponse dekResponse = await database.CreateDataEncryptionKeyAsync(dekId, this.metadata1);
            Assert.AreEqual(HttpStatusCode.Created, dekResponse.StatusCode);
            Assert.AreEqual(requestCharge, dekResponse.RequestCharge);
            Assert.IsNotNull(dekResponse.ETag);

            DataEncryptionKeyProperties dekProperties = dekResponse.Resource;
            Assert.IsNotNull(dekProperties);
            Assert.AreEqual(dekResponse.ETag, dekProperties.ETag);
            Assert.AreEqual(dekId, dekProperties.Id);

            Assert.AreEqual(1, encryptionTestHandler.Received.Count);
            RequestMessage createDekRequestMessage = encryptionTestHandler.Received[0];
            Assert.AreEqual(ResourceType.ClientEncryptionKey, createDekRequestMessage.ResourceType);
            Assert.AreEqual(OperationType.Create, createDekRequestMessage.OperationType);

            Assert.IsTrue(encryptionTestHandler.Deks.ContainsKey(dekId));
            DataEncryptionKeyProperties serverDekProperties = encryptionTestHandler.Deks[dekId];
            Assert.IsTrue(serverDekProperties.Equals(dekProperties));

            // Make sure we didn't push anything else in the JSON (such as raw DEK) by comparing JSON properties
            // to properties exposed in DataEncryptionKeyProperties.
            createDekRequestMessage.Content.Position = 0; // test assumption that the client uses MemoryStream
            JObject jObj = JObject.Parse(await new StreamReader(createDekRequestMessage.Content).ReadToEndAsync());
            IEnumerable<string> dekPropertiesPropertyNames = GetJsonPropertyNamesForType(typeof(DataEncryptionKeyProperties));

            foreach (JProperty property in jObj.Properties())
            {
                Assert.IsTrue(dekPropertiesPropertyNames.Contains(property.Name));
            }

            // Key wrap metadata should be the only "object" child in the JSON (given current properties in DataEncryptionKeyProperties)
            IEnumerable<JToken> objectChildren = jObj.PropertyValues().Where(v => v.Type == JTokenType.Object);
            Assert.AreEqual(1, objectChildren.Count());
            JObject keyWrapMetadataJObj = (JObject)objectChildren.First();
            Assert.AreEqual(Constants.Properties.KeyWrapMetadata, ((JProperty)keyWrapMetadataJObj.Parent).Name);

            IEnumerable<string> keyWrapMetadataPropertyNames = GetJsonPropertyNamesForType(typeof(KeyWrapMetadata));
            foreach (JProperty property in keyWrapMetadataJObj.Properties())
            {
                Assert.IsTrue(keyWrapMetadataPropertyNames.Contains(property.Name));
            }

            IEnumerable<byte> expectedWrappedKey = this.VerifyWrap(this.dek, this.metadata1);
            this.mockKeyWrapProvider.VerifyNoOtherCalls();

            Assert.IsTrue(expectedWrappedKey.SequenceEqual(dekProperties.WrappedDataEncryptionKey));
        }

        [TestMethod]
        public async Task EncryptionUTRewrapDek()
        {
            EncryptionTestHandler encryptionTestHandler = new EncryptionTestHandler();
            Container container = this.GetContainerWithMockSetup(encryptionTestHandler);
            Database database = ((ContainerCore)container).Database;

            string dekId = "mydek";
            DataEncryptionKeyResponse createResponse = await database.CreateDataEncryptionKeyAsync(dekId, this.metadata1);
            DataEncryptionKeyProperties createdProperties = createResponse.Resource;
            Assert.AreEqual(HttpStatusCode.Created, createResponse.StatusCode);
            this.VerifyWrap(this.dek, this.metadata1);

            DataEncryptionKey dek = database.GetDataEncryptionKey(dekId);
            DataEncryptionKeyResponse rewrapResponse = await dek.RewrapAsync(this.metadata2);
            DataEncryptionKeyProperties rewrappedProperties = rewrapResponse.Resource;
            Assert.IsNotNull(rewrappedProperties);

            Assert.AreEqual(dekId, rewrappedProperties.Id);
            Assert.AreEqual(createdProperties.CreatedTime, rewrappedProperties.CreatedTime);
            Assert.IsNotNull(rewrappedProperties.LastModified);
            Assert.AreNotEqual(createdProperties.LastModified, rewrappedProperties.LastModified);
            Assert.AreEqual(createdProperties.ResourceId, rewrappedProperties.ResourceId);
            Assert.AreEqual(createdProperties.SelfLink, rewrappedProperties.SelfLink);

            IEnumerable<byte> expectedRewrappedKey = this.dek.Select(b => (byte)(b + 2));
            Assert.IsTrue(expectedRewrappedKey.SequenceEqual(rewrappedProperties.WrappedDataEncryptionKey));

            Assert.AreEqual(new KeyWrapMetadata(this.metadata2.Value + this.metadataUpdateSuffix), rewrappedProperties.KeyWrapMetadata);

            Assert.AreEqual(2, encryptionTestHandler.Received.Count);
            RequestMessage rewrapRequestMessage = encryptionTestHandler.Received[1];
            Assert.AreEqual(ResourceType.ClientEncryptionKey, rewrapRequestMessage.ResourceType);
            Assert.AreEqual(OperationType.Replace, rewrapRequestMessage.OperationType);
            Assert.AreEqual(createResponse.ETag, rewrapRequestMessage.Headers[HttpConstants.HttpHeaders.IfMatch]);

            Assert.IsTrue(encryptionTestHandler.Deks.ContainsKey(dekId));
            DataEncryptionKeyProperties serverDekProperties = encryptionTestHandler.Deks[dekId];
            Assert.IsTrue(serverDekProperties.Equals(rewrappedProperties));

            this.VerifyWrap(this.dek, this.metadata2);
            this.mockKeyWrapProvider.VerifyNoOtherCalls();
        }

        [TestMethod]
        public async Task EncryptionUTCreateItemWithUnknownDek()
        {
            EncryptionTestHandler encryptionTestHandler = new EncryptionTestHandler();
            Container container = this.GetContainerWithMockSetup(encryptionTestHandler);
            Database database = ((ContainerCore)container).Database;

            MyItem item = EncryptionUnitTests.GetNewItem();
            try
            {
                await container.CreateItemAsync(
                    item,
                    new PartitionKey(item.PK),
                    new ItemRequestOptions
                    {
                        EncryptionOptions = new EncryptionOptions
                        {
                            DataEncryptionKey = database.GetDataEncryptionKey("random")
                        }
                    });

                Assert.Fail();
            }
            catch(CosmosException ex)
            {
                Assert.IsTrue(ex.Message.Contains(ClientResources.DataEncryptionKeyNotFound));
            }
        }

        [TestMethod]
        public async Task EncryptionUTCreateItem()
        {
            EncryptionTestHandler encryptionTestHandler = new EncryptionTestHandler();
            Container container = this.GetContainerWithMockSetup(encryptionTestHandler);
            Database database = ((ContainerCore)container).Database;

            string dekId = "mydek";
            DataEncryptionKeyResponse dekResponse = await database.CreateDataEncryptionKeyAsync(dekId, this.metadata1);
            Assert.AreEqual(HttpStatusCode.Created, dekResponse.StatusCode);
            MyItem item = await EncryptionUnitTests.CreateItemAsync(container, dekId);

            // Validate server state
            Assert.IsTrue(encryptionTestHandler.Items.TryGetValue(item.Id, out JObject serverItem));
            Assert.IsNotNull(serverItem);
            Assert.AreEqual(item.Id, serverItem.Property(Constants.Properties.Id).Value.Value<string>());
            Assert.AreEqual(item.PK, serverItem.Property(nameof(MyItem.PK)).Value.Value<string>());
            Assert.IsNull(serverItem.Property(nameof(MyItem.EncStr1)));
            Assert.IsNull(serverItem.Property(nameof(MyItem.EncInt)));

            JProperty eiJProp = serverItem.Property(Constants.Properties.EncryptedInfo);
            Assert.IsNotNull(eiJProp);
            Assert.IsNotNull(eiJProp.Value);
            Assert.AreEqual(JTokenType.Object, eiJProp.Value.Type);
            EncryptionProperties encryptionPropertiesAtServer = ((JObject)eiJProp.Value).ToObject<EncryptionProperties>();

            Assert.IsNotNull(encryptionPropertiesAtServer);
            Assert.AreEqual(dekResponse.Resource.ResourceId, encryptionPropertiesAtServer.DataEncryptionKeyRid);
            Assert.AreEqual(1, encryptionPropertiesAtServer.EncryptionAlgorithmId);
            Assert.AreEqual(1, encryptionPropertiesAtServer.EncryptionFormatVersion);
            Assert.IsNotNull(encryptionPropertiesAtServer.EncryptedData);

            JObject decryptedJObj = EncryptionUnitTests.ParseStream(new MemoryStream(encryptionPropertiesAtServer.EncryptedData.Reverse().ToArray()));
            Assert.AreEqual(2, decryptedJObj.Properties().Count());
            Assert.AreEqual(item.EncStr1, decryptedJObj.Property(nameof(MyItem.EncStr1)).Value.Value<string>());
            Assert.AreEqual(item.EncInt, decryptedJObj.Property(nameof(MyItem.EncInt)).Value.Value<int>());
        }

        [TestMethod]
        public async Task EncryptionUTReadItem()
        {
            EncryptionTestHandler encryptionTestHandler = new EncryptionTestHandler();
            Container container = this.GetContainerWithMockSetup(encryptionTestHandler);
            Database database = ((ContainerCore)container).Database;

            string dekId = "mydek";
            DataEncryptionKeyResponse dekResponse = await database.CreateDataEncryptionKeyAsync(dekId, this.metadata1);
            Assert.AreEqual(HttpStatusCode.Created, dekResponse.StatusCode);
            MyItem item = await EncryptionUnitTests.CreateItemAsync(container, dekId);

            ItemResponse<MyItem> readResponse = await container.ReadItemAsync<MyItem>(item.Id, new PartitionKey(item.PK));
            Assert.AreEqual(item, readResponse.Resource);
        }

        private static async Task<MyItem> CreateItemAsync(Container container, string dekId)
        {
            Database database = ((ContainerCore)container).Database;

            MyItem item = EncryptionUnitTests.GetNewItem();

            ItemResponse<MyItem> response = await container.CreateItemAsync<MyItem>(
                item,
                requestOptions: new ItemRequestOptions
                {
                    EncryptionOptions = new EncryptionOptions
                    {
                        DataEncryptionKey = database.GetDataEncryptionKey(dekId)
                    }
                });

            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
            Assert.AreEqual(item, response.Resource);
            return item;
        }

        private static MyItem GetNewItem()
        {
            return new MyItem()
            {
                Id = Guid.NewGuid().ToString(),
                PK = "pk",
                EncStr1 = "sensitive",
                EncInt = 10000
            };
        }

        private static IEnumerable<string> GetJsonPropertyNamesForType(Type type)
        {
            return type
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(p => Attribute.GetCustomAttribute(p, typeof(JsonPropertyAttribute)) != null)
                .Select(p => ((JsonPropertyAttribute)Attribute.GetCustomAttribute(p, typeof(JsonPropertyAttribute))).PropertyName);
        }

        private IEnumerable<byte> VerifyWrap(IEnumerable<byte> dek, KeyWrapMetadata inputMetadata)
        {
            this.mockKeyWrapProvider.Verify(m => m.WrapKeyAsync(
                It.Is<byte[]>(key => key.SequenceEqual(dek)),
                inputMetadata,
                It.IsAny<CancellationToken>()),
            Times.Exactly(1));

            IEnumerable<byte> expectedWrappedKey = null;
            if (inputMetadata == this.metadata1)
            {

                expectedWrappedKey = dek.Select(b => (byte)(b + 1));
            }
            else if (inputMetadata == this.metadata2)
            {
                expectedWrappedKey = dek.Select(b => (byte)(b + 2));
            }
            else
            {
                Assert.Fail();
            }

            // Verify we did unwrap to check on the wrapping
            KeyWrapMetadata expectedUpdatedMetadata = new KeyWrapMetadata(inputMetadata.Value + this.metadataUpdateSuffix);
            this.VerifyUnwrap(expectedWrappedKey, expectedUpdatedMetadata);

            return expectedWrappedKey;
        }

        private void VerifyUnwrap(IEnumerable<byte> wrappedDek, KeyWrapMetadata inputMetadata)
        {
            this.mockKeyWrapProvider.Verify(m => m.UnwrapKeyAsync(
                It.Is<byte[]>(wrappedKey => wrappedKey.SequenceEqual(wrappedDek)),
                inputMetadata,
                It.IsAny<CancellationToken>()),
            Times.Exactly(1));
        }

        private Container GetContainer()
        {
            CosmosClientBuilder cosmosClientBuilder = new CosmosClientBuilder("http://localhost", Guid.NewGuid().ToString());
            CosmosClient client = cosmosClientBuilder.Build(new MockDocumentClient());
            DatabaseCore database = new DatabaseCore(client.ClientContext, EncryptionUnitTests.DatabaseId);
            return new ContainerCore(client.ClientContext, database, EncryptionUnitTests.ContainerId);
        }

        private Container GetContainerWithMockSetup(EncryptionTestHandler testHandler)
        {
            this.mockKeyWrapProvider = new Mock<KeyWrapProvider>();
            this.mockKeyWrapProvider.Setup(m => m.WrapKeyAsync(It.IsAny<byte[]>(), It.IsAny<KeyWrapMetadata>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((byte[] key, KeyWrapMetadata metadata, CancellationToken cancellationToken) =>
                {
                    KeyWrapMetadata responseMetadata = new KeyWrapMetadata(metadata.Value + this.metadataUpdateSuffix);
                    int moveBy = metadata.Value == this.metadata1.Value ? 1 : 2;
                    return new KeyWrapResponse(key.Select(b => (byte)(b + moveBy)).ToArray(), responseMetadata);
                });
            this.mockKeyWrapProvider.Setup(m => m.UnwrapKeyAsync(It.IsAny<byte[]>(), It.IsAny<KeyWrapMetadata>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((byte[] wrappedKey, KeyWrapMetadata metadata, CancellationToken cancellationToken) =>
                {
                    int moveBy = metadata.Value == this.metadata1.Value + this.metadataUpdateSuffix ? 1 : 2;
                    return new KeyUnwrapResponse(wrappedKey.Select(b => (byte)(b - moveBy)).ToArray(), this.cacheTTL);
                });

            CosmosClient client = this.GetClient(testHandler);

            this.mockEncryptionAlgorithm = new Mock<EncryptionAlgorithm>();
            this.mockEncryptionAlgorithm.Setup(m => m.EncryptData(It.IsAny<byte[]>()))
                .Returns((byte[] plainText) => plainText.Reverse().ToArray());
            this.mockEncryptionAlgorithm.Setup(m => m.DecryptData(It.IsAny<byte[]>()))
                .Returns((byte[] cipherText) => cipherText.Reverse().ToArray());
            this.mockEncryptionAlgorithm.SetupGet(m => m.AlgorithmName).Returns(AeadAes256CbcHmac256Algorithm.AlgorithmNameConstant);

            this.mockDatabaseCore = new Mock<DatabaseCore>(client.ClientContext, EncryptionUnitTests.DatabaseId);
            this.mockDatabaseCore.CallBase = true;
            this.mockDatabaseCore.Setup(m => m.GetDataEncryptionKey(It.IsAny<string>()))
             .Returns((string id) =>
             {
                 Mock<DataEncryptionKeyCore> mockDekCore = new Mock<DataEncryptionKeyCore>(client.ClientContext, this.mockDatabaseCore.Object, id);
                 mockDekCore.CallBase = true;
                 mockDekCore.Setup(m => m.GenerateKey()).Returns(this.dek);
                 mockDekCore.Setup(m => m.GetEncryptionAlgorithm(It.IsAny<byte[]>(), It.IsAny<EncryptionType>()))
                    .Returns(this.mockEncryptionAlgorithm.Object);
                 return new DataEncryptionKeyInlineCore(mockDekCore.Object);
             });

            ContainerCore container = new ContainerCore(client.ClientContext, this.mockDatabaseCore.Object, EncryptionUnitTests.ContainerId);
            return container;
        }

        private CosmosClient GetClient(EncryptionTestHandler testHandler)
        {
            return MockCosmosUtil.CreateMockCosmosClient((builder) => builder.AddCustomHandlers(testHandler).WithKeyWrapProvider(this.mockKeyWrapProvider.Object));
        }

        private static JObject ParseStream(Stream stream)
        {
            return JObject.Load(new JsonTextReader(new StreamReader(stream)));
        }

        private class MyItem
        {
            [JsonProperty(PropertyName = Constants.Properties.Id, NullValueHandling = NullValueHandling.Ignore)]
            public string Id { get; set; }

            public string PK { get; set; }

            [CosmosEncrypt]
            public string EncStr1 { get; set; }

            [CosmosEncrypt]
            public int EncInt { get; set; }

            // todo: byte array, parts of objects, structures, enum

            public override bool Equals(object obj)
            {
                MyItem item = obj as MyItem;
                return item != null &&
                       this.Id == item.Id &&
                       this.PK == item.PK &&
                       this.EncStr1 == item.EncStr1 &&
                       this.EncInt == item.EncInt;
            }

            public override int GetHashCode()
            {
                int hashCode = -307924070;
                hashCode = (hashCode * -1521134295) + EqualityComparer<string>.Default.GetHashCode(this.Id);
                hashCode = (hashCode * -1521134295) + EqualityComparer<string>.Default.GetHashCode(this.PK);
                hashCode = (hashCode * -1521134295) + EqualityComparer<string>.Default.GetHashCode(this.EncStr1);
                hashCode = (hashCode * -1521134295) + this.EncInt.GetHashCode();
                return hashCode;
            }
        }

        private class EncryptionPropertiesComparer : IEqualityComparer<EncryptionProperties>
        {
            public bool Equals(EncryptionProperties x, EncryptionProperties y)
            {
                if (x == null && y == null) return true;
                if (x == null || y == null) return false;
                return x.EncryptionFormatVersion == y.EncryptionFormatVersion
                    && x.EncryptionAlgorithmId == y.EncryptionAlgorithmId
                    && x.DataEncryptionKeyRid == y.DataEncryptionKeyRid
                    && x.EncryptedData.SequenceEqual(y.EncryptedData);
            }

            public int GetHashCode(EncryptionProperties obj)
            {
                // sufficient for test impl.
                return 0;
            }
        }

        private class EncryptionTestHandler : TestHandler
        {
            private readonly Func<RequestMessage, Task<ResponseMessage>> func;

            private readonly CosmosJsonDotNetSerializer serializer = new CosmosJsonDotNetSerializer();


            public EncryptionTestHandler(Func<RequestMessage, Task<ResponseMessage>> func = null)
            {
                this.func = func;
            }

            public ConcurrentDictionary<string, DataEncryptionKeyProperties> Deks { get; } = new ConcurrentDictionary<string, DataEncryptionKeyProperties>();


            public ConcurrentDictionary<string, JObject> Items { get; } = new ConcurrentDictionary<string, JObject>();

            public List<RequestMessage> Received { get; } = new List<RequestMessage>();

            public override async Task<ResponseMessage> SendAsync(
                RequestMessage request,
                CancellationToken cancellationToken)
            {
                // We clone the request message as the Content is disposed before we can use it in the test assertions later.
                this.Received.Add(EncryptionTestHandler.CloneRequestMessage(request));

                if (this.func != null)
                {
                    return await this.func(request);
                }

                HttpStatusCode httpStatusCode = HttpStatusCode.InternalServerError;

                if (request.ResourceType == ResourceType.ClientEncryptionKey)
                {
                    DataEncryptionKeyProperties dekProperties = null;
                    if (request.OperationType == OperationType.Create)
                    {
                        dekProperties = this.serializer.FromStream<DataEncryptionKeyProperties>(request.Content);
                        string databaseRid = ResourceId.NewDatabaseId(1).ToString();
                        dekProperties.ResourceId = ResourceId.NewClientEncryptionKeyId(databaseRid, (uint)this.Received.Count).ToString();
                        dekProperties.CreatedTime = DateTime.UtcNow;
                        dekProperties.LastModified = dekProperties.CreatedTime;
                        dekProperties.ETag = Guid.NewGuid().ToString();
                        dekProperties.SelfLink = string.Format(
                            "dbs/{0}/{1}/{2}/",
                           databaseRid,
                            Paths.ClientEncryptionKeysPathSegment,
                            dekProperties.ResourceId);

                        httpStatusCode = HttpStatusCode.Created;
                        if (!this.Deks.TryAdd(dekProperties.Id, dekProperties))
                        {
                            httpStatusCode = HttpStatusCode.Conflict;
                        }
                    }
                    else if (request.OperationType == OperationType.Read)
                    {
                        string dekId = EncryptionTestHandler.ParseDekUri(request.RequestUri);
                        httpStatusCode = HttpStatusCode.OK;
                        if (!this.Deks.TryGetValue(dekId, out dekProperties))
                        {
                            httpStatusCode = HttpStatusCode.NotFound;
                        }
                    }
                    else if(request.OperationType == OperationType.Replace)
                    {
                        string dekId = EncryptionTestHandler.ParseDekUri(request.RequestUri);
                        dekProperties = this.serializer.FromStream<DataEncryptionKeyProperties>(request.Content);
                        dekProperties.LastModified = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                        dekProperties.ETag = Guid.NewGuid().ToString();

                        httpStatusCode = HttpStatusCode.OK;
                        if (!this.Deks.TryGetValue(dekId, out DataEncryptionKeyProperties existingDekProperties))
                        {
                            httpStatusCode = HttpStatusCode.NotFound;
                        }

                        if(!this.Deks.TryUpdate(dekId, dekProperties, existingDekProperties)) { throw new InvalidOperationException("Concurrency not handled in tests."); }
                    }
                    else if (request.OperationType == OperationType.Delete)
                    {
                        string dekId = EncryptionTestHandler.ParseDekUri(request.RequestUri);
                        httpStatusCode = HttpStatusCode.NoContent;
                        if (!this.Deks.TryRemove(dekId, out _))
                        {
                            httpStatusCode = HttpStatusCode.NotFound;
                        }
                    }

                    ResponseMessage responseMessage = new ResponseMessage(httpStatusCode, request)
                    {
                        Content = dekProperties != null ? this.serializer.ToStream(dekProperties) : null,
                    };

                    responseMessage.Headers.RequestCharge = EncryptionUnitTests.requestCharge;
                    responseMessage.Headers.ETag = dekProperties?.ETag;
                    return responseMessage;
                }
                else if(request.ResourceType == ResourceType.Document)
                {
                    JObject item = null;
                    if (request.OperationType == OperationType.Create)
                    {
                        item = EncryptionUnitTests.ParseStream(request.Content);
                        string itemId = item.Property("id").Value.Value<string>();

                        httpStatusCode = HttpStatusCode.Created;
                        if (!this.Items.TryAdd(itemId, item))
                        {
                            httpStatusCode = HttpStatusCode.Conflict;
                        }
                    }
                    else if (request.OperationType == OperationType.Read)
                    {
                        string itemId = EncryptionTestHandler.ParseItemUri(request.RequestUri);
                        httpStatusCode = HttpStatusCode.OK;
                        if (!this.Items.TryGetValue(itemId, out item))
                        {
                            httpStatusCode = HttpStatusCode.NotFound;
                        }
                    }
                    else if (request.OperationType == OperationType.Replace)
                    {
                        string itemId = EncryptionTestHandler.ParseItemUri(request.RequestUri);
                        item = EncryptionUnitTests.ParseStream(request.Content);

                        httpStatusCode = HttpStatusCode.OK;
                        if (!this.Items.TryGetValue(itemId, out JObject existingItem))
                        {
                            httpStatusCode = HttpStatusCode.NotFound;
                        }

                        if (!this.Items.TryUpdate(itemId, item, existingItem)) { throw new InvalidOperationException("Concurrency not handled in tests."); }
                    }
                    else if (request.OperationType == OperationType.Delete)
                    {
                        string itemId = EncryptionTestHandler.ParseItemUri(request.RequestUri);
                        httpStatusCode = HttpStatusCode.NoContent;
                        if (!this.Items.TryRemove(itemId, out _))
                        {
                            httpStatusCode = HttpStatusCode.NotFound;
                        }
                    }

                    ResponseMessage responseMessage = new ResponseMessage(httpStatusCode, request)
                    {
                        Content = item != null ? this.serializer.ToStream(item) : null,
                    };

                    responseMessage.Headers.RequestCharge = EncryptionUnitTests.requestCharge;
                    return responseMessage;

                }

                return new ResponseMessage(httpStatusCode, request);
            }

            private static RequestMessage CloneRequestMessage(RequestMessage request)
            {
                MemoryStream contentClone = null;
                if (request.Content != null)
                {
                    // assuming seekable Stream
                    contentClone = new MemoryStream((int)request.Content.Length);
                    request.Content.CopyTo(contentClone);
                    request.Content.Position = 0;
                }

                RequestMessage clone = new RequestMessage(request.Method, request.RequestUri)
                {
                    OperationType = request.OperationType,
                    ResourceType = request.ResourceType,
                    RequestOptions = request.RequestOptions,
                    Content = contentClone
                };

                foreach (string x in request.Headers)
                {
                    clone.Headers.Set(x, request.Headers[x]);
                }

                return clone;
            }

            private static string ParseItemUri(Uri requestUri)
            {
                string[] segments = requestUri.OriginalString.Split("/", StringSplitOptions.RemoveEmptyEntries);
                Assert.AreEqual(6, segments.Length);
                Assert.AreEqual(Paths.DatabasesPathSegment, segments[0]);
                Assert.AreEqual(EncryptionUnitTests.DatabaseId, segments[1]);
                Assert.AreEqual(Paths.CollectionsPathSegment, segments[2]);
                Assert.AreEqual(EncryptionUnitTests.ContainerId, segments[3]);
                Assert.AreEqual(Paths.DocumentsPathSegment, segments[4]);
                return segments[5];
            }

            private static string ParseDekUri(Uri requestUri)
            {
                string[] segments = requestUri.OriginalString.Split("/", StringSplitOptions.RemoveEmptyEntries);
                Assert.AreEqual(4, segments.Length);
                Assert.AreEqual(Paths.DatabasesPathSegment, segments[0]);
                Assert.AreEqual(EncryptionUnitTests.DatabaseId, segments[1]);
                Assert.AreEqual(Paths.ClientEncryptionKeysPathSegment, segments[2]);
                return segments[3];
            }
        }
    }
}
