# Crowdhandler .NET SDK

The Crowdhandler .NET SDK provides a simple and efficient way to integrate the Crowdhandler API into your .NET projects.

## GateKeeper Class

The GateKeeper class is the core of the Crowdhandler .NET SDK, containing a generic validation API that can be dropped into other projects.

### Properties

- ApiEndpoint: The Crowdhandler API URL.
- PublicApiKey: The Crowdhandler public API Key.
- PrivateApiKey: The Crowdhandler private API Key.
- WaitingRoomEndpoint: The Crowdhandler Waiting room URL.
- Exclusions: The Crowdhandler Exclusions regular expression.
- APIRequestTimeout: The Crowdhandler API Request Timeout in seconds.
- RoomCacheTTL: The Crowdhandler RoomCache Time To Live in seconds.

### Constructor

```csharp
GateKeeper(String publicKey, String privateKey, String apiEndpoint, String waitingRoomEndpoint, String exclusions, String apiRequestTimeout, String roomCacheTTL, String safetyNetSlug)
```

Initializes a new instance of the GateKeeper class with the provided keys and endpoints. If any value is not provided, it will use the default value or the corresponding configuration value.

### Methods

### Validate

```csharp
public virtual ValidateResult Validate(Uri url, String CookieJSON = "", RoomConfig room = null)
```

**Parameters:**

- url: The URL to test.
- CookieJSON: A JSON formatted string containing validation information. If not provided, validation is attempted against parameters provided in the URL query string.
- room: A set of Crowdhandler room configurations. If not provided, these are fetched using your API key via HTTP.

**Returns**: 
A ValidateResult object containing the result of the validation and additional data.

#### ValidateResult Struct

- **string** Action: The action to be taken ("allow" or "redirect").
- **string** redirectUrl: The URL to redirect to if the action is "redirect".
- **string** targetUrl: The target URL for validation.
- **bool** setCookie: A boolean indicating if a cookie should be set.
- **string** cookieValue: The value of the cookie to be set.
- **string** code: The Crowdhandler code.
- **string** token: The Crowdhandler token.
- **bool** expired: A boolean indicating if the signature is expired.

### ValidateSignature (cookie)

This method validates a signature when only cookie data is available.

```csharp
public virtual ValidateSignatureResponse ValidateSignature(List<CookieSignature> CandidateSignatures, CookieData cookie, String token, RoomConfig room)
```

**Parameters:**

- List\<CookieSignature\> CandidateSignatures: A list of candidate signatures to validate.
- CookieData cookie: The cookie data to validate.
- String token: The token to use for validation.
- RoomConfig room: The room configuration to use for validation.

**Returns**:
A ValidateSignatureResponse object with the validation result.

### ValidateSignature (url)

This method validates a signature when when URL parameters are available..

```csharp
public virtual ValidateSignatureResponse ValidateSignature(String Signature, DateTime requested, String token, RoomConfig room)
```

**Parameters:**

- String Signature: The signature to validate.
- DateTime requested: The requested timestamp.
- String token: The token to use for validation.
- RoomConfig room: The room configuration to use for validation.

**Returns**:
A ValidateSignatureResponse object with the validation result.


#### ValidateSignatureResponse Struct

- **bool** success: Indicates whether the signature is valid or not.
- **bool** expired: Indicates whether the signature has expired or not.

### GetCookieData
This method converts a JSON object into a structured CookieData object.

```csharp
public virtual CookieData getCookieData(String JSONCookieData)
```

**Parameters:**

- **string** JSONCookieData: The JSON string representing the cookie data.

**Returns**: 

A CookieData object or null if the input is null or empty.

### MatchRoom
This method checks if the provided host and URL path match any of the rooms in the provided room configuration.

```csharp
public virtual RoomConfig MatchRoom(string host, string path, List<RoomConfig> rooms)
```

**Parameters:**

- **string** host: The hostname.
- **string** path: The URL path and query string.
- **List<RoomConfig>** rooms: The list of room configurations.

**Returns**: 

First matched RoomConfig, or null if one could not be found.

### IsRoomMatch
This method checks if the provided host and URL path match any of the rooms found via the Crowdhandler API.

```csharp
public virtual RoomConfig IsRoomMatch(string host, string path)
```

**Parameters:**

- **string** string host: The hostname.
- **string** path: The URL path and query string.

**Returns**: 

First matched RoomConfig, or null if one could not be found.

### GetConfigValue
This method looks up an application configuration value from Web.config or App.config.

```csharp
protected virtual String getConfigValue(String settingName, Boolean required)
```

**Parameters:**

- **string** settingName: The config value name to look up.
- **bool** required: The config value name to look up.

**Returns**: 

Config value as a string.

### getRoomConfig

This method retrieves a list of room configurations using the GetApiClient method.

```csharp
public virtual List<RoomConfig> getRoomConfig()
```

**Returns**: 

Config value as a string.

A List<RoomConfig> containing the room configurations.


