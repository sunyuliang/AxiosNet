namespace AxiosNet.Cli.Diagnostics;

/// <summary>警告编号：写进控制台摘要与 <c>--report</c>，也是警告去重键的一部分。</summary>
/// <remarks>
/// 编号是<b>对外契约</b>：<c>--report</c> 的消费方按它分类统计，改名等于改契约。只有取值稳定才放常量，
/// 文案仍留在各报警点旁边（一句话只服务一处判定，集中起来反而看不到上下文）。
/// </remarks>
internal static class WarningCodes
{
    /// <summary>allOf 引用成环，停止展开属性。</summary>
    internal const string ALLOF_CYCLE = "ALLOF_CYCLE";

    /// <summary>anyOf 无法用声明式接口表达，按 JsonElement 生成。</summary>
    internal const string ANYOF_FALLBACK = "ANYOF_FALLBACK";

    /// <summary>apiKey 安全方案无法用 [Authorize] 表达。</summary>
    internal const string APIKEY_SECURITY = "APIKEY_SECURITY";

    /// <summary>数组缺少对象形式的 items，按 JsonElement[] 生成。</summary>
    internal const string ARRAY_WITHOUT_ITEMS = "ARRAY_WITHOUT_ITEMS";

    /// <summary>引用不存在（$ref 指向的节点缺失）。</summary>
    internal const string BROKEN_REF = "BROKEN_REF";

    /// <summary>requestBody 无法解析，跳过该操作。</summary>
    internal const string BROKEN_REQUEST_BODY = "BROKEN_REQUEST_BODY";

    /// <summary>cookie 参数无法在接口声明中表达，跳过该参数。</summary>
    internal const string COOKIE_PARAMETER = "COOKIE_PARAMETER";

    /// <summary>数组参数用 deepObject 编码，AxiosNet 无对应格式，按 Multi 生成。</summary>
    internal const string DEEP_OBJECT_ARRAY = "DEEP_OBJECT_ARRAY";

    /// <summary>没有 2xx 响应，按 default 响应生成。</summary>
    internal const string DEFAULT_RESPONSE = "DEFAULT_RESPONSE";

    /// <summary>类型没有任何属性，生成空类。</summary>
    internal const string EMPTY_MODEL = "EMPTY_MODEL";

    /// <summary>枚举值不能作为 C# 成员名，改为常量类 + string 属性。</summary>
    internal const string ENUM_AS_STRING = "ENUM_AS_STRING";

    /// <summary>类型上的 enum 未处理，按原类型生成。</summary>
    internal const string ENUM_IGNORED = "ENUM_IGNORED";

    /// <summary>不支持外部引用（非本文档内的 $ref），按 JsonElement 处理。</summary>
    internal const string EXTERNAL_REF = "EXTERNAL_REF";

    /// <summary>formData 参数缺少 name，忽略该参数。</summary>
    internal const string FORM_PARAMETER_WITHOUT_NAME = "FORM_PARAMETER_WITHOUT_NAME";

    /// <summary>路径模板含无法识别的占位符。</summary>
    internal const string INVALID_PATH_TEMPLATE = "INVALID_PATH_TEMPLATE";

    /// <summary>上次生成留下的清单文件无法读取，按首次生成处理。</summary>
    internal const string MANIFEST_INVALID = "MANIFEST_INVALID";

    /// <summary>请求头参数由框架管理，跳过。</summary>
    internal const string MANAGED_HEADER_PARAMETER = "MANAGED_HEADER_PARAMETER";

    /// <summary>路径模板占位符没有对应的参数声明。</summary>
    internal const string MISSING_PATH_PARAMETER = "MISSING_PATH_PARAMETER";

    /// <summary>操作缺少 responses，按无返回值生成。</summary>
    internal const string MISSING_RESPONSES = "MISSING_RESPONSES";

    /// <summary>同一操作的查询集合格式不一致，统一取第一种。</summary>
    internal const string MIXED_COLLECTION_FORMATS = "MIXED_COLLECTION_FORMATS";

    /// <summary>multipart 请求体声明了 additionalProperties，未知字段不能展开为部件。</summary>
    internal const string MULTIPART_ADDITIONAL_PROPERTIES = "MULTIPART_ADDITIONAL_PROPERTIES";

    /// <summary>部件是复杂对象，表单编码结果可能与服务端预期不一致。</summary>
    internal const string MULTIPART_OBJECT_PART = "MULTIPART_OBJECT_PART";

    /// <summary>multipart 请求体不是可展开的对象 schema。</summary>
    internal const string MULTIPART_UNSUPPORTED = "MULTIPART_UNSUPPORTED";

    /// <summary>请求体声明了多个媒体类型，取其一。</summary>
    internal const string MULTIPLE_REQUEST_CONTENT = "MULTIPLE_REQUEST_CONTENT";

    /// <summary>响应声明了多个媒体类型，取其一。</summary>
    internal const string MULTIPLE_RESPONSE_CONTENT = "MULTIPLE_RESPONSE_CONTENT";

    /// <summary>存在多个 2xx 响应，取其一。</summary>
    internal const string MULTIPLE_SUCCESS_RESPONSES = "MULTIPLE_SUCCESS_RESPONSES";

    /// <summary>schema 的 type 声明了多个取值，按 JsonElement 处理。</summary>
    internal const string MULTIPLE_TYPES = "MULTIPLE_TYPES";

    /// <summary>security 要求同时满足多个方案，[Authorize] 只能表达其一。</summary>
    internal const string MULTI_SCHEME_SECURITY = "MULTI_SCHEME_SECURITY";

    /// <summary>没有 2xx / 2XX / default 响应，按无返回值生成。</summary>
    internal const string NO_SUCCESS_RESPONSE = "NO_SUCCESS_RESPONSE";

    /// <summary>数字枚举按数值类型生成。</summary>
    internal const string NUMERIC_ENUM = "NUMERIC_ENUM";

    /// <summary>数字枚举含非整数或超出 64 位整数范围的值，按 double 生成。</summary>
    internal const string NUMERIC_ENUM_FRACTION = "NUMERIC_ENUM_FRACTION";

    /// <summary>oauth2 安全方案无法用 [Authorize] 表达。</summary>
    internal const string OAUTH_SECURITY = "OAUTH_SECURITY";

    /// <summary>oneOf 无法用声明式接口表达，按 JsonElement 生成。</summary>
    internal const string ONEOF_FALLBACK = "ONEOF_FALLBACK";

    /// <summary>操作或参数无法用声明式接口表达，整条操作跳过。</summary>
    internal const string OPERATION_SKIPPED = "OPERATION_SKIPPED";

    /// <summary>路径参数声明为可选，仍按必填生成。</summary>
    internal const string OPTIONAL_PATH_PARAMETER = "OPTIONAL_PATH_PARAMETER";

    /// <summary>参数缺少 name，跳过该参数。</summary>
    internal const string PARAMETER_WITHOUT_NAME = "PARAMETER_WITHOUT_NAME";

    /// <summary>路径级的 body / formData 参数不受支持，忽略。</summary>
    internal const string PATH_BODY_PARAMETER = "PATH_BODY_PARAMETER";

    /// <summary>别名循环引用，按 JsonElement 处理。</summary>
    internal const string REF_CYCLE = "REF_CYCLE";

    /// <summary>响应的媒体类型没有 schema，按 JsonElement 生成。</summary>
    internal const string RESPONSE_WITHOUT_SCHEMA = "RESPONSE_WITHOUT_SCHEMA";

    /// <summary>schema 嵌套过深，按 JsonElement 处理。</summary>
    internal const string SCHEMA_DEPTH = "SCHEMA_DEPTH";

    /// <summary>查询参数使用 simple 风格，按 Csv 生成。</summary>
    internal const string SIMPLE_QUERY_STYLE = "SIMPLE_QUERY_STYLE";

    /// <summary>上次生成的文件本次不再生成（加 --force 可删除）。</summary>
    internal const string STALE_FILE = "STALE_FILE";

    /// <summary>流式响应的 schema 是数组，按元素类型逐帧生成。</summary>
    internal const string STREAM_ARRAY_FRAME = "STREAM_ARRAY_FRAME";

    /// <summary>流式响应需要 IAsyncEnumerable 返回形态，--response-wrapper none 不生效。</summary>
    internal const string STREAM_REQUIRES_WRAPPER = "STREAM_REQUIRES_WRAPPER";

    /// <summary>元组数组（prefixItems）按 JsonElement[] 生成。</summary>
    internal const string TUPLE_ARRAY = "TUPLE_ARRAY";

    /// <summary>查询参数的 collectionFormat 取值未知，按 Multi 生成。</summary>
    internal const string UNKNOWN_COLLECTION_FORMAT = "UNKNOWN_COLLECTION_FORMAT";

    /// <summary>参数的 in 取值不受支持，跳过该参数。</summary>
    internal const string UNKNOWN_PARAMETER_LOCATION = "UNKNOWN_PARAMETER_LOCATION";

    /// <summary>查询参数的 style 取值未知，按 Multi 生成。</summary>
    internal const string UNKNOWN_QUERY_STYLE = "UNKNOWN_QUERY_STYLE";

    /// <summary>schema 的类型未知，按 JsonElement 处理。</summary>
    internal const string UNKNOWN_SCHEMA_TYPE = "UNKNOWN_SCHEMA_TYPE";

    /// <summary>安全方案类型无法用 [Authorize] 表达。</summary>
    internal const string UNKNOWN_SECURITY = "UNKNOWN_SECURITY";

    /// <summary>http 安全方案的 scheme 不是 bearer / basic。</summary>
    internal const string UNSUPPORTED_HTTP_SECURITY = "UNSUPPORTED_HTTP_SECURITY";

    /// <summary>路径参数的 style 不是 simple，跳过该操作。</summary>
    internal const string UNSUPPORTED_PATH_STYLE = "UNSUPPORTED_PATH_STYLE";

    /// <summary>查询参数使用 deepObject 编码，AxiosNet 无对应格式。</summary>
    internal const string UNSUPPORTED_QUERY_STYLE = "UNSUPPORTED_QUERY_STYLE";

    /// <summary>路径参数不在路径模板中，移除。</summary>
    internal const string UNUSED_PATH_PARAMETER = "UNUSED_PATH_PARAMETER";

    /// <summary>OpenAPI webhooks 不会生成为客户端接口方法。</summary>
    internal const string WEBHOOKS_IGNORED = "WEBHOOKS_IGNORED";
}
