using System;
using System.Collections.Generic;
using Crowdhandler.NETsdk.JSONTypes;
using Xunit;

namespace Crowdhandler.NETsdk.Tests
{
    public class SignatureTests
    {
        private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        [Fact]
        public void UrlSignature_MatchesApiFormula()
        {
            var gk = Fixture.NewGateKeeper();
            string requested = Now();
            var r = gk.ValidateSignature(Fixture.Signature(requested), DateTime.Parse(requested, null, System.Globalization.DateTimeStyles.AdjustToUniversal), Fixture.Token, Fixture.Room());
            Assert.True(r.success);
            Assert.False(r.expired);
        }

        [Fact]
        public void UrlSignature_KnownVector()
        {
            // Pinned so the formula can never drift silently: sha256(sha256(priv) + slug + queueActivatesOn + token + requested)
            string requested = "2024-06-01T12:00:00Z";
            string expected = Fixture.Sha256(Fixture.Sha256(Fixture.PrivateKey) + "main-sale" + "2024-01-01T00:00:00Z" + "tok0M7SBFAp9J8kK" + requested);
            Assert.Equal(expected, Fixture.Signature(requested));
            Assert.Equal(64, expected.Length);
        }

        [Fact]
        public void UrlSignature_ExpiredWhenOlderThanRoomTimeout()
        {
            var gk = Fixture.NewGateKeeper();
            string requested = DateTime.UtcNow.AddMinutes(-16).ToString("yyyy-MM-ddTHH:mm:ssZ");
            var r = gk.ValidateSignature(Fixture.Signature(requested), DateTime.Parse(requested, null, System.Globalization.DateTimeStyles.AdjustToUniversal), Fixture.Token, Fixture.Room(timeout: 15));
            Assert.False(r.success);
            Assert.True(r.expired);
        }

        [Fact]
        public void UrlSignature_WrongTokenOrRoomFails()
        {
            var gk = Fixture.NewGateKeeper();
            string requested = Now();
            var sig = Fixture.Signature(requested);
            Assert.False(gk.ValidateSignature(sig, DateTime.UtcNow, "tok0OTHER000000", Fixture.Room()).success);
            Assert.False(gk.ValidateSignature(sig, DateTime.UtcNow, Fixture.Token, Fixture.Room(slug: "other-room")).success);
            Assert.False(gk.ValidateSignature("", DateTime.UtcNow, Fixture.Token, Fixture.Room()).success);
            Assert.False(gk.ValidateSignature(sig, DateTime.UtcNow, Fixture.Token, null).success);
        }

        [Fact]
        public void UrlSignature_QueueActivatesOnWithoutZoneIsTreatedAsUtc()
        {
            // Regression: the old code called ToUniversalTime() on an Unspecified-kind value, shifting it by the server's time zone.
            var gk = Fixture.NewGateKeeper();
            string requested = Now();
            var room = Fixture.Room();
            room.queueActivatesOn = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
            Assert.True(gk.ValidateSignature(Fixture.Signature(requested), DateTime.Parse(requested, null, System.Globalization.DateTimeStyles.AdjustToUniversal), Fixture.Token, room).success);
        }

        [Fact]
        public void CookieSignature_MatchesAnyStoredSignature_NewestFirst()
        {
            var gk = Fixture.NewGateKeeper();
            string gen1 = DateTime.UtcNow.AddHours(-2).ToString("yyyy-MM-ddTHH:mm:ssZ");
            string gen2 = DateTime.UtcNow.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm:ssZ");
            var cookie = gk.getCookieData(Fixture.CookieJson(sigs: new[] { Fixture.Signature(gen1), Fixture.Signature(gen2) }, gens: new[] { gen1, gen2 }));
            var r = gk.ValidateSignature(cookie.tokens[0].signatures, cookie, Fixture.Token, Fixture.Room());
            Assert.True(r.success);
        }

        [Fact]
        public void CookieSignature_AcceptsLegacySecondsTouched()
        {
            var gk = Fixture.NewGateKeeper();
            string gen = Now();
            ulong touchedSeconds = Util.DateTimeToUnixTimeStamp(DateTime.UtcNow);
            var cookie = gk.getCookieData(Fixture.CookieJson(sigs: new[] { Fixture.Signature(gen) }, gens: new[] { gen }, touched: touchedSeconds, touchedSig: Fixture.TouchedSig(touchedSeconds)));
            Assert.True(gk.ValidateSignature(cookie.tokens[0].signatures, cookie, Fixture.Token, Fixture.Room()).success);
        }

        [Fact]
        public void CookieSignature_AcceptsMillisecondTouched_AsWrittenByJsSdk()
        {
            var gk = Fixture.NewGateKeeper();
            string gen = Now();
            ulong touchedMs = Util.DateTimeToUnixTimeStampMs(DateTime.UtcNow);
            var cookie = gk.getCookieData(Fixture.CookieJson(sigs: new[] { Fixture.Signature(gen) }, gens: new[] { gen }, touched: touchedMs, touchedSig: Fixture.TouchedSig(touchedMs)));
            Assert.True(gk.ValidateSignature(cookie.tokens[0].signatures, cookie, Fixture.Token, Fixture.Room()).success);
        }

        [Fact]
        public void CookieSignature_TamperedTouchedSigIsExpired()
        {
            var gk = Fixture.NewGateKeeper();
            string gen = Now();
            var cookie = gk.getCookieData(Fixture.CookieJson(sigs: new[] { Fixture.Signature(gen) }, gens: new[] { gen }, touchedSig: new string('0', 64)));
            var r = gk.ValidateSignature(cookie.tokens[0].signatures, cookie, Fixture.Token, Fixture.Room());
            Assert.False(r.success);
            Assert.True(r.expired);
        }

        [Fact]
        public void CookieSignature_TimedOutSessionIsExpired()
        {
            var gk = Fixture.NewGateKeeper();
            string gen = DateTime.UtcNow.AddMinutes(-30).ToString("yyyy-MM-ddTHH:mm:ssZ");
            ulong touched = Util.DateTimeToUnixTimeStampMs(DateTime.UtcNow.AddMinutes(-30));
            var cookie = gk.getCookieData(Fixture.CookieJson(sigs: new[] { Fixture.Signature(gen) }, gens: new[] { gen }, touched: touched, touchedSig: Fixture.TouchedSig(touched)));
            var r = gk.ValidateSignature(cookie.tokens[0].signatures, cookie, Fixture.Token, Fixture.Room(timeout: 15));
            Assert.False(r.success);
            Assert.True(r.expired);
        }

        [Fact]
        public void CookieSignature_NullEntriesNeverThrow()
        {
            var gk = Fixture.NewGateKeeper();
            var cookie = new CookieData { tokens = new List<CookieToken> { new CookieToken { token = Fixture.Token, signatures = new List<CookieSignature> { null, new CookieSignature { sig = null } } } } };
            Assert.False(gk.ValidateSignature(cookie.tokens[0].signatures, cookie, Fixture.Token, Fixture.Room()).success);
            Assert.False(gk.ValidateSignature(null, cookie, Fixture.Token, Fixture.Room()).success);
            Assert.False(gk.ValidateSignature(new List<CookieSignature>(), null, Fixture.Token, Fixture.Room()).success);
        }
    }
}
