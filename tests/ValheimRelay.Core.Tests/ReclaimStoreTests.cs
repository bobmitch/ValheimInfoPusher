using System;
using ValheimRelay.Core.Identity;
using ValheimRelay.Core.Session;
using Xunit;

namespace ValheimRelay.Core.Tests
{
    public class ReclaimStoreTests
    {
        private readonly InMemoryReclaimStorage _storage = new();
        private readonly FakeLog _log = new();

        private ReclaimStore NewStore() => new(_storage, _log);

        [Fact]
        public void RoundTripsAcrossAProcessRestart()
        {
            NewStore().Put("world-1", new ReclaimEntry("K7MQ2XR4", "tok", 3, 1000));

            var reloaded = NewStore().Get("world-1")!;
            Assert.Equal("K7MQ2XR4", reloaded.Code);
            Assert.Equal("tok", reloaded.Token);
            Assert.Equal(3, reloaded.Epoch);
            Assert.Equal(1000, reloaded.SavedAtUnixMs);
        }

        [Fact]
        public void KeepsWorldsSeparate()
        {
            var store = NewStore();
            store.Put("world-1", new ReclaimEntry("AAAAAAAA", "t1", 1, 0));
            store.Put("world-2", new ReclaimEntry("BBBBBBBB", "t2", 1, 0));

            var reloaded = NewStore();
            Assert.Equal("AAAAAAAA", reloaded.Get("world-1")!.Code);
            Assert.Equal("BBBBBBBB", reloaded.Get("world-2")!.Code);
            Assert.Null(reloaded.Get("world-3"));
        }

        [Fact]
        public void ForgetRemovesOnlyTheNamedWorld()
        {
            var store = NewStore();
            store.Put("world-1", new ReclaimEntry("AAAAAAAA", "t1", 1, 0));
            store.Put("world-2", new ReclaimEntry("BBBBBBBB", "t2", 1, 0));
            store.Forget("world-1");

            var reloaded = NewStore();
            Assert.Null(reloaded.Get("world-1"));
            Assert.NotNull(reloaded.Get("world-2"));
        }

        [Fact]
        public void TheSaltIsCreatedOnceAndReusedForever()
        {
            var first = NewStore().Salt;
            Assert.False(string.IsNullOrEmpty(first));
            Assert.Equal(first, NewStore().Salt);
            Assert.True(StableUid.TryDecodeSalt(first, out _));
        }

        [Fact]
        public void TheSaltSurvivesWritingWorldEntries()
        {
            var store = NewStore();
            var salt = store.Salt;
            store.Put("world-1", new ReclaimEntry("AAAAAAAA", "t1", 1, 0));

            Assert.Equal(salt, NewStore().Salt);
        }

        [Fact]
        public void ACorruptStoreStartsFreshInsteadOfFailingTheSession()
        {
            _storage.Contents = "{ this is not json";

            var store = NewStore();
            Assert.Null(store.Get("world-1"));
            Assert.True(_log.Contains("not valid JSON"));
        }

        [Fact]
        public void EntriesMissingACodeOrTokenAreSkipped()
        {
            _storage.Contents =
                "{\"version\":1,\"worlds\":{" +
                "\"good\":{\"code\":\"AAAAAAAA\",\"token\":\"t\"}," +
                "\"noToken\":{\"code\":\"BBBBBBBB\"}," +
                "\"noCode\":{\"token\":\"t\"}," +
                "\"notAnObject\":42}}";

            var store = NewStore();
            Assert.NotNull(store.Get("good"));
            Assert.Null(store.Get("noToken"));
            Assert.Null(store.Get("noCode"));
            Assert.Null(store.Get("notAnObject"));
        }

        [Fact]
        public void AnUnreadableStoreDoesNotThrow()
        {
            var store = new ReclaimStore(new ThrowingStorage(), _log);

            Assert.Null(store.Get("world-1"));
            store.Put("world-1", new ReclaimEntry("A", "t", 1, 0));
            Assert.True(_log.Contains("could not"));
        }

        [Fact]
        public void ACorruptStoreIsNotEchoedIntoTheLogBecauseItHoldsTokens()
        {
            _storage.Contents = "{\"worlds\":{\"w\":{\"token\":\"super-secret\"} oops";

            NewStore().Get("w");

            Assert.DoesNotContain(_log.Lines, l => l.Contains("super-secret"));
        }

        [Fact]
        public void EmptyWorldUidsAreIgnoredRatherThanCreatingAJunkEntry()
        {
            var store = NewStore();
            store.Put("", new ReclaimEntry("A", "t", 1, 0));

            Assert.Null(store.Get(""));
            Assert.Equal(0, _storage.Writes);
        }

        private sealed class ThrowingStorage : IReclaimStorage
        {
            public string? Read() => throw new InvalidOperationException("disk gone");
            public void Write(string contents) => throw new InvalidOperationException("disk gone");
        }
    }
}
