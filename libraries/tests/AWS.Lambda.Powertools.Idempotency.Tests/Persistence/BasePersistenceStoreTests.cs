/*
 * Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License").
 * You may not use this file except in compliance with the License.
 * A copy of the License is located at
 * 
 *  http://aws.amazon.com/apache2.0
 * 
 * or in the "license" file accompanying this file. This file is distributed
 * on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either
 * express or implied. See the License for the specific language governing
 * permissions and limitations under the License.
 */

using System;
using System.IO;
using System.Threading.Tasks;
using Amazon.Lambda.APIGatewayEvents;
using AWS.Lambda.Powertools.Idempotency.Exceptions;
using AWS.Lambda.Powertools.Idempotency.Internal;
using AWS.Lambda.Powertools.Idempotency.Persistence;
using AWS.Lambda.Powertools.Idempotency.Tests.Model;
using FluentAssertions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AWS.Lambda.Powertools.Idempotency.Tests.Persistence;

public class BasePersistenceStoreTests
{
    class InMemoryPersistenceStore : BasePersistenceStore
    {
        private string _validationHash = null;
        public DataRecord? DataRecord = null;
        public int Status = -1;
        public override Task<DataRecord> GetRecord(string idempotencyKey)
        {
            Status = 0;
            var dataRecord = new DataRecord(
                idempotencyKey,
                DataRecord.DataRecordStatus.INPROGRESS,
                DateTimeOffset.UtcNow.AddSeconds(3600).ToUnixTimeSeconds(),
                "Response",
                _validationHash);
            return Task.FromResult(dataRecord);
        }

        public override Task PutRecord(DataRecord record, DateTimeOffset now)
        {
            DataRecord = record;
            Status = 1;
            return Task.CompletedTask;
        }

        public override Task UpdateRecord(DataRecord record)
        {
            DataRecord = record;
            Status = 2;
            return Task.CompletedTask;
        }

        public override Task DeleteRecord(string idempotencyKey)
        {
            DataRecord = null;
            Status = 3;
            return Task.CompletedTask;
        }
    }
    
    [Fact]
    public async Task SaveInProgress_WhenDefaultConfig_ShouldSaveRecordInStore()
    {
        // Arrange
        var persistenceStore = new InMemoryPersistenceStore();
        var request = LoadApiGatewayProxyRequest();
        
        persistenceStore.Configure(IdempotencyConfig.Builder().Build(), null);
        
        DateTimeOffset now = DateTimeOffset.UtcNow;
        
        // Act
        await persistenceStore.SaveInProgress(JToken.FromObject(request), now);

        // Assert
        var dr = persistenceStore.DataRecord;
        dr.Status.Should().Be(DataRecord.DataRecordStatus.INPROGRESS);
        dr.ExpiryTimestamp.Should().Be(now.AddSeconds(3600).ToUnixTimeSeconds());
        dr.ResponseData.Should().BeNull();
        dr.IdempotencyKey.Should().Be("testFunction#36e3de9a3270f82fb957c645178dfab9");
        dr.PayloadHash.Should().BeEmpty();
        persistenceStore.Status.Should().Be(1);
    }

    [Fact]
    public async Task SaveInProgress_WhenKeyJmesPathIsSet_ShouldSaveRecordInStore_WithIdempotencyKeyEqualsKeyJmesPath()
    {
        // Arrange
        var persistenceStore = new InMemoryPersistenceStore();
        var request = LoadApiGatewayProxyRequest();
        
        persistenceStore.Configure(IdempotencyConfig.Builder()
            .WithEventKeyJmesPath("powertools_json(Body).id")
            .Build(), "myfunc");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        
        // Act
        await persistenceStore.SaveInProgress(JToken.FromObject(request), now);
        
        // Assert
        var dr = persistenceStore.DataRecord;
        dr.Status.Should().Be(DataRecord.DataRecordStatus.INPROGRESS);
        dr.ExpiryTimestamp.Should().Be(now.AddSeconds(3600).ToUnixTimeSeconds());
        dr.ResponseData.Should().BeNull();
        dr.IdempotencyKey.Should().Be("testFunction.myfunc#2fef178cc82be5ce3da6c5e0466a6182");
        dr.PayloadHash.Should().BeEmpty();
        persistenceStore.Status.Should().Be(1);
    }
    
    
    [Fact]
    public async Task SaveInProgress_WhenJMESPath_NotFound_ShouldThrowException()
    {
        // Arrange
        var persistenceStore = new InMemoryPersistenceStore();
        var request = LoadApiGatewayProxyRequest();
        
        persistenceStore.Configure(IdempotencyConfig.Builder()
            .WithEventKeyJmesPath("unavailable")
            .WithThrowOnNoIdempotencyKey(true) // should throw
            .Build(), "");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        
        // Act
        Func<Task> act = async () => await persistenceStore.SaveInProgress(JToken.FromObject(request), now);
        
        // Assert
        await act.Should()
            .ThrowAsync<IdempotencyKeyException>()
            .WithMessage("No data found to create a hashed idempotency key");
        
        persistenceStore.Status.Should().Be(-1);
    }
    
    [Fact]
    public async Task SaveInProgress_WhenJMESpath_NotFound_ShouldNotThrowException()
    {
        // Arrange
        var persistenceStore = new InMemoryPersistenceStore();
        var request = LoadApiGatewayProxyRequest();
        
        persistenceStore.Configure(IdempotencyConfig.Builder()
            .WithEventKeyJmesPath("unavailable")
            .Build(), "");
        
        DateTimeOffset now = DateTimeOffset.UtcNow;
        
        // Act
        await persistenceStore.SaveInProgress(JToken.FromObject(request), now);

        // Assert
        DataRecord dr = persistenceStore.DataRecord;
        dr.Status.Should().Be(DataRecord.DataRecordStatus.INPROGRESS);
        persistenceStore.Status.Should().Be(1);
    }
    
    [Fact]
    public async Task SaveInProgress_WhenLocalCacheIsSet_AndNotExpired_ShouldThrowException()
    {
        // Arrange
        var persistenceStore = new InMemoryPersistenceStore();
        var request = LoadApiGatewayProxyRequest();
        
        LRUCache<string, DataRecord> cache = new ((int) 2);
        persistenceStore.Configure(IdempotencyConfig.Builder()
            .WithUseLocalCache(true)
            .WithEventKeyJmesPath("powertools_json(Body).id")
            .Build(), null, cache);
        
        DateTimeOffset now = DateTimeOffset.UtcNow;
        cache.Set("testFunction#2fef178cc82be5ce3da6c5e0466a6182",
            new DataRecord(
                "testFunction#2fef178cc82be5ce3da6c5e0466a6182",
                DataRecord.DataRecordStatus.INPROGRESS,
                now.AddSeconds(3600).ToUnixTimeSeconds(),
                null, null)
        );
        
        // Act
        Func<Task> act = () => persistenceStore.SaveInProgress(JToken.FromObject(request), now);

        // Assert
        await act.Should()
            .ThrowAsync<IdempotencyItemAlreadyExistsException>();

        persistenceStore.Status.Should().Be(-1);
    }
    
    [Fact]
    public async Task SaveInProgress_WhenLocalCacheIsSetButExpired_ShouldRemoveFromCache()
    {
        // Arrange
        var persistenceStore = new InMemoryPersistenceStore();
        var request = LoadApiGatewayProxyRequest();
        
        LRUCache<string, DataRecord> cache = new ((int) 2);
        persistenceStore.Configure(IdempotencyConfig.Builder()
            .WithEventKeyJmesPath("powertools_json(Body).id")
            .WithUseLocalCache(true)
            .WithExpiration(TimeSpan.FromSeconds(2))
            .Build(), null, cache);
        
        DateTimeOffset now = DateTimeOffset.UtcNow;
        cache.Set("testFunction#2fef178cc82be5ce3da6c5e0466a6182",
            new DataRecord(
                "testFunction#2fef178cc82be5ce3da6c5e0466a6182",
                DataRecord.DataRecordStatus.INPROGRESS,
                now.AddSeconds(-3).ToUnixTimeSeconds(),
                null, null)
        );
        
        // Act
        await persistenceStore.SaveInProgress(JToken.FromObject(request), now);

        // Assert
        DataRecord dr = persistenceStore.DataRecord;
        dr.Status.Should().Be(DataRecord.DataRecordStatus.INPROGRESS);
        cache.Count.Should().Be(0);
        persistenceStore.Status.Should().Be(1);
    }
    
    ////// Save Success
    
    [Fact]
    public async Task SaveSuccess_WhenDefaultConfig_ShouldUpdateRecord() 
    {
        // Arrange
        var persistenceStore = new InMemoryPersistenceStore();
        var request = LoadApiGatewayProxyRequest();
        LRUCache<string, DataRecord> cache = new ((int) 2);
        persistenceStore.Configure(IdempotencyConfig.Builder().Build(), null, cache);

        Product product = new Product(34543, "product", 42);
        
        DateTimeOffset now = DateTimeOffset.UtcNow;
        
        // Act
        await persistenceStore.SaveSuccess(JToken.FromObject(request), product, now);

        // Assert
        DataRecord dr = persistenceStore.DataRecord;
        dr.Status.Should().Be(DataRecord.DataRecordStatus.COMPLETED);
        dr.ExpiryTimestamp.Should().Be(now.AddSeconds(3600).ToUnixTimeSeconds());
        dr.ResponseData.Should().Be(JsonConvert.SerializeObject(product));
        dr.IdempotencyKey.Should().Be("testFunction#36e3de9a3270f82fb957c645178dfab9");
        dr.PayloadHash.Should().BeEmpty();
        persistenceStore.Status.Should().Be(2);
        cache.Count.Should().Be(0);
    }
    
    [Fact]
    public async Task SaveSuccess_WhenCacheEnabled_ShouldSaveInCache()
    {
        // Arrange
        var persistenceStore = new InMemoryPersistenceStore();
        var request = LoadApiGatewayProxyRequest();
        LRUCache<string, DataRecord> cache = new ((int) 2);
        
        persistenceStore.Configure(IdempotencyConfig.Builder()
            .WithUseLocalCache(true).Build(), null, cache);

        Product product = new Product(34543, "product", 42);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        
        // Act
        await persistenceStore.SaveSuccess(JToken.FromObject(request), product, now);

        // Assert
        persistenceStore.Status.Should().Be(2);
        cache.Count.Should().Be(1);
    
        var foundDataRecord = cache.TryGet("testFunction#36e3de9a3270f82fb957c645178dfab9", out DataRecord record);
        foundDataRecord.Should().BeTrue();
        record.Status.Should().Be(DataRecord.DataRecordStatus.COMPLETED);
        record.ExpiryTimestamp.Should().Be(now.AddSeconds(3600).ToUnixTimeSeconds());
        record.ResponseData.Should().Be(JsonConvert.SerializeObject(product));
        record.IdempotencyKey.Should().Be("testFunction#36e3de9a3270f82fb957c645178dfab9");
        record.PayloadHash.Should().BeEmpty();
    }
    
    /// Get Record
    
    [Fact]
    public async Task GetRecord_WhenRecordIsInStore_ShouldReturnRecordFromPersistence() 
    {
        // Arrange
        var persistenceStore = new InMemoryPersistenceStore();
        var request = LoadApiGatewayProxyRequest();
        
        LRUCache<string, DataRecord> cache = new((int) 2);
        persistenceStore.Configure(IdempotencyConfig.Builder().Build(), "myfunc", cache);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        
        // Act
        DataRecord record = await persistenceStore.GetRecord(JToken.FromObject(request), now);
        
        // Assert
        record.IdempotencyKey.Should().Be("testFunction.myfunc#36e3de9a3270f82fb957c645178dfab9");
        record.Status.Should().Be(DataRecord.DataRecordStatus.INPROGRESS);
        record.ResponseData.Should().Be("Response");
        persistenceStore.Status.Should().Be(0);
    }
    
    [Fact]
    public async Task GetRecord_WhenCacheEnabledNotExpired_ShouldReturnRecordFromCache() 
    {
        // Arrange
        var persistenceStore = new InMemoryPersistenceStore();
        var request = LoadApiGatewayProxyRequest();
        LRUCache<string, DataRecord> cache = new((int) 2);
        
        persistenceStore.Configure(IdempotencyConfig.Builder()
            .WithUseLocalCache(true).Build(), "myfunc", cache);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DataRecord dr = new DataRecord(
            "testFunction.myfunc#36e3de9a3270f82fb957c645178dfab9",
            DataRecord.DataRecordStatus.COMPLETED,
            now.AddSeconds(3600).ToUnixTimeSeconds(),
            "result of the function",
            null);
        cache.Set("testFunction.myfunc#36e3de9a3270f82fb957c645178dfab9", dr);

        // Act
        DataRecord record = await persistenceStore.GetRecord(JToken.FromObject(request), now);
        
        // Assert
        record.IdempotencyKey.Should().Be("testFunction.myfunc#36e3de9a3270f82fb957c645178dfab9");
        record.Status.Should().Be(DataRecord.DataRecordStatus.COMPLETED);
        record.ResponseData.Should().Be("result of the function");
        persistenceStore.Status.Should().Be(-1);
    }
    
    [Fact]
    public async Task GetRecord_WhenLocalCacheEnabledButRecordExpired_ShouldReturnRecordFromPersistence() 
    {
        // Arrange
        var persistenceStore = new InMemoryPersistenceStore();
        var request = LoadApiGatewayProxyRequest();
        LRUCache<string, DataRecord> cache = new((int) 2);
        persistenceStore.Configure(IdempotencyConfig.Builder()
            .WithUseLocalCache(true).Build(), "myfunc", cache);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DataRecord dr = new DataRecord(
            "testFunction.myfunc#36e3de9a3270f82fb957c645178dfab9",
            DataRecord.DataRecordStatus.COMPLETED,
            now.AddSeconds(-3).ToUnixTimeSeconds(),
            "result of the function",
            null);
        cache.Set("testFunction.myfunc#36e3de9a3270f82fb957c645178dfab9", dr);

        // Act
        DataRecord record = await persistenceStore.GetRecord(JToken.FromObject(request), now);
        
        // Assert
        record.IdempotencyKey.Should().Be("testFunction.myfunc#36e3de9a3270f82fb957c645178dfab9");
        record.Status.Should().Be(DataRecord.DataRecordStatus.INPROGRESS);
        record.ResponseData.Should().Be("Response");
        persistenceStore.Status.Should().Be(0);
        cache.Count.Should().Be(0);
    }
    
    [Fact]
    public async Task GetRecord_WhenInvalidPayload_ShouldThrowValidationException()
    {
        // Arrange
        var persistenceStore = new InMemoryPersistenceStore();
        var request = LoadApiGatewayProxyRequest();
        
        persistenceStore.Configure(IdempotencyConfig.Builder()
                .WithEventKeyJmesPath("powertools_json(Body).id")
                .WithPayloadValidationJmesPath("powertools_json(Body).message")
                .Build(),
            "myfunc");

        var validationHash = "different hash"; // "Lambda rocks" ==> 70c24d88041893f7fbab4105b76fd9e1
        DateTimeOffset now = DateTimeOffset.UtcNow;
        
        // Act
        Func<Task> act = () => persistenceStore.GetRecord(JToken.FromObject(request), now);
        
        // Assert
        await act.Should().ThrowAsync<IdempotencyValidationException>();
    }
    
    // Delete Record
    [Fact]
    public async Task DeleteRecord_WhenRecordExist_ShouldDeleteRecordFromPersistence() 
    {
        // Arrange
        var persistenceStore = new InMemoryPersistenceStore();
        var request = LoadApiGatewayProxyRequest();
        
        persistenceStore.Configure(IdempotencyConfig.Builder().Build(), null);

        // Act
        await persistenceStore.DeleteRecord(JToken.FromObject(request), new ArithmeticException());
        
        // Assert
        persistenceStore.Status.Should().Be(3);
    }
    
    [Fact]
    public async Task DeleteRecord_WhenLocalCacheEnabled_ShouldDeleteRecordFromCache() 
    {
        // Arrange
        var persistenceStore = new InMemoryPersistenceStore();
        var request = LoadApiGatewayProxyRequest();
        LRUCache<string, DataRecord> cache = new ((int) 2);
        persistenceStore.Configure(IdempotencyConfig.Builder()
            .WithUseLocalCache(true).Build(), null, cache);

        cache.Set("testFunction#36e3de9a3270f82fb957c645178dfab9",
            new DataRecord("testFunction#36e3de9a3270f82fb957c645178dfab9", 
                DataRecord.DataRecordStatus.COMPLETED,
                123,
                null, null));
        
        // Act
        await persistenceStore.DeleteRecord(JToken.FromObject(request), new ArithmeticException());
        
        // Assert
        persistenceStore.Status.Should().Be(3);
        cache.Count.Should().Be(0); 
    }
    
    [Fact]
    public void GenerateHash_WhenInputIsString_ShouldGenerateMd5ofString() 
    {
        // Arrange
        var persistenceStore = new InMemoryPersistenceStore();
        persistenceStore.Configure(IdempotencyConfig.Builder().Build(), null);
        string expectedHash = "70c24d88041893f7fbab4105b76fd9e1"; // MD5(Lambda rocks)
        
        // Act
        string generatedHash = persistenceStore.GenerateHash(new JValue("Lambda rocks"));
        
        // Assert
        generatedHash.Should().Be(expectedHash);
    }
    
    [Fact]
    public void GenerateHash_WhenInputIsObject_ShouldGenerateMd5ofJsonObject()
    {
        // Arrange
        var persistenceStore = new InMemoryPersistenceStore();
        persistenceStore.Configure(IdempotencyConfig.Builder().Build(), null);
        Product product = new Product(42, "Product", 12);
        string expectedHash = "87dd2e12074c65c9bac728795a6ebb45"; // MD5({"Id":42,"Name":"Product","Price":12.0})
        
        // Act
        string generatedHash = persistenceStore.GenerateHash(JToken.FromObject(product));
        
        // Assert
        generatedHash.Should().Be(expectedHash);
    }

    [Fact]
    public void GenerateHash_WhenInputIsDouble_ShouldGenerateMd5ofDouble() 
    {
        // Arrange
        var persistenceStore = new InMemoryPersistenceStore();
        persistenceStore.Configure(IdempotencyConfig.Builder().Build(), null);
        string expectedHash = "bb84c94278119c8838649706df4db42b"; // MD5(256.42)
        
        // Act
        var generatedHash = persistenceStore.GenerateHash(new JValue(256.42));
        
        // Assert
        generatedHash.Should().Be(expectedHash);
    }
    
    private static APIGatewayProxyRequest LoadApiGatewayProxyRequest()
    {
        var eventJson = File.ReadAllText("./resources/apigw_event.json");
        var request = JsonConvert.DeserializeObject<APIGatewayProxyRequest>(eventJson);
        return request!;
    }
}