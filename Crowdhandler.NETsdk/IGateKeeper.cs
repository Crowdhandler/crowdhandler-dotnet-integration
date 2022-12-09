using Crowdhandler.NETsdk.JSONTypes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Crowdhandler.NETsdk
{
    public interface IGateKeeper
    {
        GateKeeper.ValidateResult Validate(Uri url, String CookieJSON = "", RoomConfig room = null);
        String WaitingRoomEndpoint { get; set; }
        String PublicApiKey { get; set; }
        String PrivateApiKey { get; set; }
        String ApiEndpoint { get; set; }
    }
}
