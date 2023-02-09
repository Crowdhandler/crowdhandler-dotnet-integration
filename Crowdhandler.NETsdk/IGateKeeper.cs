using Crowdhandler.NETsdk.JSONTypes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Crowdhandler.NETsdk
{
    public interface IGateKeeper
    {
        GateKeeper.ValidateResult Validate(Uri url, String CookieJSON = "", RoomConfig room = null);
        String PublicApiKey { get; set; }
        String PrivateApiKey { get; set; }
        String ApiEndpoint { get; set; }
        String WaitingRoomEndpoint { get; set; }
        String Exclusions { get; set; }
        String APIRequestTimeout { get; set; }
        String RoomCacheTTL { get; set; }
    }
}
