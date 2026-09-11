using System;
using System.Configuration;
using System.IO;
using Xunit;

namespace Crowdhandler.NETsdk.Tests
{
    /// <summary>
    /// ConfigurationManager latches its config file path on first use anywhere in the process, so the test assembly points it
    /// at a scratch file before any test runs; tests then only change the file's contents and refresh.
    /// </summary>
    internal static class TestConfigFile
    {
        public static readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "crowdhandler-tests-" + Guid.NewGuid().ToString("N") + ".config");

        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void Init()
        {
            File.WriteAllText(Path, "<?xml version=\"1.0\"?><configuration><appSettings/></configuration>");
            AppDomain.CurrentDomain.SetData("APP_CONFIG_FILE", Path);
        }
    }

    /// <summary>The Web.config / App.config path: values come from ConfigurationManager.AppSettings when not passed explicitly.</summary>
    public class AppSettingsTests
    {
        private static void UseConfig(string appSettingsXml)
        {
            File.WriteAllText(TestConfigFile.Path, "<?xml version=\"1.0\"?><configuration><appSettings>" + appSettingsXml + "</appSettings></configuration>");
            ConfigurationManager.RefreshSection("appSettings");
        }

        [Fact]
        public void AllValuesReadFromAppSettings_WhenNotSupplied()
        {
            UseConfig(
                "<add key=\"CROWDHANDLER_PUBLIC_KEY\" value=\"pub-from-config\"/>" +
                "<add key=\"CROWDHANDLER_PRIVATE_KEY\" value=\"priv-from-config\"/>" +
                "<add key=\"CROWDHANDLER_API_ENDPOINT\" value=\"https://api.config.test\"/>" +
                "<add key=\"CROWDHANDLER_WR_ENDPOINT\" value=\"https://wait.config.test\"/>" +
                "<add key=\"CROWDHANDLER_EXCLUSIONS_REGEX\" value=\"^/skip\"/>" +
                "<add key=\"CROWDHANDLER_API_REQUEST_TIMEOUT\" value=\"7\"/>" +
                "<add key=\"CROWDHANDLER_ROOM_CACHE_TIME\" value=\"11\"/>" +
                "<add key=\"CROWDHANDLER_SAFETYNET_SLUG\" value=\"safety-from-config\"/>" +
                "<add key=\"CROWDHANDLER_CHECK_IN_INTERVAL\" value=\"4.5\"/>");
            try
            {
                var gk = new GateKeeper();
                Assert.Equal("pub-from-config", gk.PublicApiKey);
                Assert.Equal("priv-from-config", gk.PrivateApiKey);
                Assert.Equal("https://api.config.test", gk.ApiEndpoint);
                Assert.Equal("https://wait.config.test", gk.WaitingRoomEndpoint);
                Assert.Equal("^/skip", gk.Exclusions);
                Assert.Equal("7", gk.APIRequestTimeout);
                Assert.Equal("11", gk.RoomCacheTTL);
                Assert.Equal("safety-from-config", gk.SafetyNetSlug);
                Assert.Equal(TimeSpan.FromMinutes(4.5), gk.CheckInInterval);

                // explicit arguments still win over config
                var explicitGk = new GateKeeper("pub-explicit", apiRequestTimeout: "2");
                Assert.Equal("pub-explicit", explicitGk.PublicApiKey);
                Assert.Equal("priv-from-config", explicitGk.PrivateApiKey);
                Assert.Equal("2", explicitGk.APIRequestTimeout);
            }
            finally
            {
                UseConfig(""); // leave no keys for other tests
            }
        }

        [Fact]
        public void MissingRequiredKey_Throws_WithTheKeyName()
        {
            UseConfig("<add key=\"CROWDHANDLER_PUBLIC_KEY\" value=\"only-public\"/>");
            try
            {
                var ex = Assert.Throws<MissingFieldException>(() => new GateKeeper());
                Assert.Contains("CROWDHANDLER_PRIVATE_KEY", ex.Message);
            }
            finally
            {
                UseConfig("");
            }
        }
    }
}
