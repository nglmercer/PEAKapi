// Adapted from https://github.com/nglmercer/REPOapi (MIT). See third-party/REPOapi-NOTICE.md.
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using PeakRestApi.Core.Models;

namespace PeakRestApi.Core.Http;

/// <summary>
/// A rendered HTTP response. The body is serialised once, at construction
/// time, so the HTTP thread never touches game objects while writing.
/// </summary>
public sealed class ApiResponse
{
    public int Status { get; }

    public string Json { get; }

    /// <summary>
    /// Optional value for the <c>Allow</c> header, used for 405 responses.
    /// </summary>
    public string? Allow { get; private set; }

    public ApiResponse(int status, object body)
    {
        Status = status;
        Json = JsonConvert.SerializeObject(body, Settings);
    }

    private ApiResponse(string json)
    {
        Status = 200;
        Json = json;
    }

    public static ApiResponse Raw(string json) => new(json);

    public static ApiResponse MethodNotAllowed(string allow) =>
        new ApiResponse(405, new { success = false, error = new { code = "METHOD_NOT_ALLOWED", message = "This endpoint requires " + allow + ".", details = (object?)null } })
        {
            Allow = allow,
        };

    public static readonly JsonSerializerSettings Settings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        MaxDepth = 16,
    };

    public static ApiResponse Error(ApiException e) =>
        new(e.Status, new { success = false, error = new { code = e.Code, message = e.Message, details = e.Details } });
}
