using System.Text.Json;
using AxiosNet.Cli.Model;
using AxiosNet.Cli.Spec;

namespace AxiosNet.Cli.Tests;

/// <summary>文档归一化与 OpenAPI → 声明式 C# 的映射口径，断言都落在最终生成文本上。</summary>
public sealed class GenerationTests
{
    /// <summary>OpenAPI 3.0 文档在接口模式下应完整映射特性（参数位置、集合格式、附件、流、枚举等）。</summary>
    [Fact]
    public void OpenApi30_MapsDeclarativeSurface()
    {
        using var run = CliHarness.Run("petstore.json", "--style", "interface");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(14, run.Operations);
        Assert.Equal(2, run.GroupCount);

        var pets = run.Read("IPetsApi.g.cs");
        Assert.Contains("public interface IPetsApi", pets);
        Assert.Contains("[Authorize]", pets);
        Assert.Contains("[QueryCollectionFormat(AxiosCollectionFormat.Multi)]", pets);
        Assert.Contains("[QueryCollectionFormat(AxiosCollectionFormat.Csv)]", pets);
        Assert.Contains("[Multipart]", pets);
        Assert.Contains("[Part] byte[] file", pets);
        Assert.Contains("[Part] string? caption", pets);
        Assert.Contains("[Stream(AxiosStreamFormat.ServerSentEvents)]", pets);
        Assert.Contains("Task<AxiosResponse<IAsyncEnumerable<StreamEventsAsyncItem>>> StreamEventsAsync(", pets);
        Assert.Contains("Task<AxiosResponse<byte[]>> DownloadAsync(", pets);
        Assert.Contains("Task<AxiosResponse> DeletePetAsync(", pets);
        Assert.Contains("[Header(\"X-Request-Id\")] string? xRequestId", pets);
        Assert.Contains("[Path] long petId", pets);
        Assert.Contains("[Obsolete(\"Deprecated in the OpenAPI document.\")]", pets);
        // 动词兜底命名：/multi 没有 operationId，不能拼出 GETMulti 这种缩写。
        Assert.Contains("Task<AxiosResponse<Pet>> GetMultiAsync(", pets);

        var auth = run.Read("IAuthApi.g.cs");
        Assert.Contains("[BodyContentType(\"application/x-www-form-urlencoded\")]", auth);
        Assert.Contains("[AllowAnonymous]", auth);
        // apiKey 无法用 [Authorize] 表达：只记警告，不生成授权声明。
        Assert.DoesNotContain("[Authorize]", auth);

        var models = run.Read("Models.g.cs");
        Assert.Contains("public sealed class Pet", models);
        Assert.Contains("public string Name { get; set; } = default!;", models);
        Assert.Contains("public long Id { get; set; }", models);
        Assert.Contains("[JsonPropertyName(\"createdAt\")]", models);
        Assert.Contains("public enum PetStatus", models);
        Assert.Contains("[JsonConverter(typeof(JsonStringEnumConverter))]", models);
        Assert.Contains("public static class WeirdStatusValues", models);

        Assert.Contains("ENUM_AS_STRING", run.Codes);
        Assert.Contains("ONEOF_FALLBACK", run.Codes);
        Assert.Contains("APIKEY_SECURITY", run.Codes);
        Assert.Contains("MULTIPLE_SUCCESS_RESPONSES", run.Codes);
        Assert.Contains("MULTIPLE_RESPONSE_CONTENT", run.Codes);
    }

    /// <summary>缺省形态是 model：每个操作生成一个请求类，正文 / 响应 / 授权都写进类型声明。</summary>
    [Fact]
    public void ModelStyle_IsDefault_EmitsOneRequestPerOperation()
    {
        using var run = CliHarness.Run("petstore.json");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("model", run.Style);
        Assert.Equal(14, run.Operations);
        Assert.Equal(
            new[] { "AuthRequests.g.cs", "Models.g.cs", "PetsRequests.g.cs", "axiosnet-gen.manifest.json", CliRun.ReportName },
            run.FileNames);

        var pets = run.Read("PetsRequests.g.cs");
        Assert.Contains("[Get(\"/pets\")]", pets);
        Assert.Contains("[Authorize]", pets);
        Assert.Contains("public sealed class ListPetsRequest : ApiRequest<Pet[]>", pets);
        Assert.Contains("public int? Limit { get; set; }", pets);
        Assert.Contains("[Query(\"status\")]", pets);
        Assert.Contains("[Header(\"X-Request-Id\")]", pets);
        Assert.Contains("public sealed class CreatePetRequest : ApiRequest<Pet>", pets);
        Assert.Contains("public NewPet Body { get; set; } = default!;", pets);
        // 无响应正文：泛型固定 object，调用方不必为 204 造一个响应类型。
        Assert.Contains("public sealed class DeletePetRequest : ApiRequest<object>", pets);
        Assert.Contains("[Obsolete(\"Deprecated in the OpenAPI document.\")]", pets);
        Assert.Contains("public const AxiosStreamFormat StreamFormat = AxiosStreamFormat.ServerSentEvents;", pets);
        Assert.Contains("config.Serialization.CollectionFormat = AxiosCollectionFormat.Csv;", pets);
        Assert.Contains("[Multipart]", pets);
        Assert.Contains("[Part(\"file\")]", pets);

        var auth = run.Read("AuthRequests.g.cs");
        Assert.Contains("[AllowAnonymous]", auth);
        Assert.DoesNotContain("[Authorize]", auth);
        // 表单正文没有方法级特性可写，改由 Configure 落到本次请求。
        Assert.Contains("protected override void Configure(AxiosConfig config)", auth);
        Assert.Contains("config.ContentType = \"application/x-www-form-urlencoded\";", auth);
    }

    /// <summary>请求类名先于 schema 占名，同名模型自动改名为 Xxx2，引用仍指向改名后的类型。</summary>
    [Fact]
    public void ModelStyle_ReservesRequestClassNames()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "T", "version": "1" },
              "paths": {
                "/pets": {
                  "get": {
                    "operationId": "listPets",
                    "responses": {
                      "200": {
                        "description": "ok",
                        "content": { "application/json": { "schema": { "$ref": "#/components/schemas/ListPetsRequest" } } }
                      }
                    }
                  }
                }
              },
              "components": {
                "schemas": { "ListPetsRequest": { "type": "object", "properties": { "name": { "type": "string" } } } }
              }
            }
            """;

        using var run = CliHarness.RunText(spec, ".json");

        Assert.Equal(0, run.ExitCode);
        // 请求类名先占位：同名 schema 让位成 ListPetsRequest2，引用关系保持正确。
        Assert.Contains(
            "public sealed class ListPetsRequest : ApiRequest<ListPetsRequest2>",
            run.Read("DefaultRequests.g.cs"));
        Assert.Contains("public sealed class ListPetsRequest2", run.Read("Models.g.cs"));
    }

    /// <summary>--single-file 把请求类与模型合到一个文件，且不再产出多余的分组文件。</summary>
    [Fact]
    public void ModelStyle_SingleFileKeepsEverythingTogether()
    {
        using var run = CliHarness.Run("petstore.json", "--single-file");

        Assert.Equal(new[] { "ApiClient.g.cs", "axiosnet-gen.manifest.json", CliRun.ReportName }, run.FileNames);
        var all = run.Read("ApiClient.g.cs");
        Assert.Contains("public sealed class ListPetsRequest", all);
        Assert.Contains("public sealed class LoginRequest", all);
        Assert.Contains("public sealed class Pet", all);
    }

    /// <summary>Swagger 2.0 的 #/definitions 引用需重写为 OAS3 路径后仍可解析，含递归引用与表单上传。</summary>
    [Fact]
    public void Swagger2_ResolvesDefinitionRefsAndForms()
    {
        using var run = CliHarness.Run("swagger2.json", "--style", "interface");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(3, run.Operations);

        var api = run.Read("IUsersApi.g.cs");
        Assert.Contains("[QueryCollectionFormat(AxiosCollectionFormat.Multi)]", api);
        Assert.Contains("[Authorize(\"Basic\")]", api);
        Assert.Contains("[Multipart]", api);
        Assert.Contains("[Part] byte[] file", api);
        Assert.Contains("Task<AxiosResponse> UploadAvatarAsync(", api);

        var models = run.Read("Models.g.cs");
        // #/definitions/User 必须重写成 #/components/schemas/User 后仍能解析，递归引用也要活着。
        Assert.Contains("public sealed class User", models);
        Assert.Contains("public User? Manager { get; set; }", models);
        Assert.Contains("public sealed class NewUser", models);
        Assert.Contains("public string? Nickname { get; set; }", models);
        Assert.Contains("public enum UserRole", models);
    }

    /// <summary>OpenAPI 3.1 的 type 数组（如 ["string","null"]）应映射为可空引用类型 / 可空值类型。</summary>
    [Fact]
    public void OpenApi31_TypeArrayNullable()
    {
        using var run = CliHarness.Run("mini-3.1.yaml");

        Assert.Equal(0, run.ExitCode);
        var models = run.Read("Models.g.cs");
        Assert.Contains("public bool Pong { get; set; }", models);
        Assert.Contains("public string? Echo { get; set; }", models);
    }

    /// <summary>--single-interface + --interface-name + --single-file 三者组合只产出一个接口文件。</summary>
    [Fact]
    public void SingleInterfaceAndSingleFile()
    {
        using var run = CliHarness.Run(
            "petstore.json",
            "--style",
            "interface",
            "--single-interface",
            "--interface-name",
            "IAllApi",
            "--single-file");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(new[] { "IAllApi.g.cs", "axiosnet-gen.manifest.json", CliRun.ReportName }, run.FileNames);
        var all = run.Read("IAllApi.g.cs");
        Assert.Contains("public interface IAllApi", all);
        Assert.Contains("public sealed class Pet", all);
        Assert.Contains("ListPetsAsync(", all);
        Assert.Contains("LoginAsync(", all);
    }

    /// <summary>别名互相引用（A → B → A）必须告警，不再靠 JsonElement 占位悄悄降级。</summary>
    [Fact]
    public void AliasCycle_IsReportedInsteadOfSilentlyDegrading()
    {
        const string Spec = """
            {
              "openapi": "3.0.0",
              "info": { "title": "alias-cycle", "version": "1.0" },
              "paths": {
                "/a": {
                  "get": {
                    "operationId": "getA",
                    "responses": {
                      "200": {
                        "description": "ok",
                        "content": { "application/json": { "schema": { "$ref": "#/components/schemas/A" } } }
                      }
                    }
                  }
                }
              },
              "components": {
                "schemas": {
                  "A": { "$ref": "#/components/schemas/B" },
                  "B": { "$ref": "#/components/schemas/A" }
                }
              }
            }
            """;

        using var run = CliHarness.RunText(Spec, ".json", "--style", "interface");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("REF_CYCLE", run.Codes);
    }

    [Fact]
    public void AliasChain_IsNotReportedAsCycle()
    {
        // 别名链 A → B → string：只是一层套一层，不是环。环检测用「解析中」集合而不是「解析过」集合，
        // 就是为了不把这种正常形态（以及同一个别名被两处引用）误报成 REF_CYCLE。
        const string Spec = """
            {
              "openapi": "3.0.0",
              "info": { "title": "alias-chain", "version": "1.0" },
              "paths": {
                "/a": {
                  "get": {
                    "operationId": "getA",
                    "responses": {
                      "200": {
                        "description": "ok",
                        "content": { "application/json": { "schema": { "$ref": "#/components/schemas/A" } } }
                      }
                    }
                  }
                }
              },
              "components": {
                "schemas": {
                  "A": { "$ref": "#/components/schemas/B" },
                  "B": { "$ref": "#/components/schemas/C" },
                  "C": { "type": "string" }
                }
              }
            }
            """;

        using var run = CliHarness.RunText(Spec, ".json", "--style", "interface");

        Assert.Equal(0, run.ExitCode);
        Assert.DoesNotContain("REF_CYCLE", run.Codes);
    }

    /// <summary>--response-wrapper none 去掉 AxiosResponse 包装；流式接口因无法表达而记警告。</summary>
    [Fact]
    public void ResponseWrapperNone_UnwrapsReturnTypes()
    {
        using var run = CliHarness.Run("petstore.json", "--style", "interface", "--response-wrapper", "none");

        var pets = run.Read("IPetsApi.g.cs");
        Assert.Contains("Task<Pet[]> ListPetsAsync(", pets);
        Assert.Contains("Task DeletePetAsync(", pets);
        Assert.Contains("STREAM_REQUIRES_WRAPPER", run.Codes);
    }

    /// <summary>对抗性文档里的坏引用、多类型、元组数组等只产生告警并继续生成，不改变退出码。</summary>
    [Fact]
    public void Adversarial_ReportsGapsWithoutFailing()
    {
        using var run = CliHarness.Run("adversarial.json", "--style", "interface");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("BROKEN_REF", run.Codes);
        Assert.Contains("MULTIPLE_TYPES", run.Codes);
        Assert.Contains("TUPLE_ARRAY", run.Codes);
        Assert.Contains("COOKIE_PARAMETER", run.Codes);
        Assert.Contains("ANYOF_FALLBACK", run.Codes);

        // anyOf 的成员类型仍要生成，调用方才能自行判别。
        var models = run.Read("Models.g.cs");
        Assert.Contains("public sealed class Left", models);
        Assert.Contains("public sealed class Right", models);
    }

    /// <summary>同一份文档用 JSON 与 YAML 表达，生成的模型文件必须逐字节相同。</summary>
    [Fact]
    public void JsonAndYaml_ProduceSameModel()
    {
        const string basePath = """
            "paths": {
              "/ping": {
                "get": {
                  "operationId": "ping",
                  "responses": {
                    "200": {
                      "description": "ok",
                      "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Pong" } } }
                    }
                  }
                }
              }
            },
            "components": { "schemas": { "Pong": { "type": "object", "properties": { "ok": { "type": "boolean" } } } } }
            """;

        var json = $$"""
            { "openapi": "3.0.3", "info": { "title": "T", "version": "1" }, {{basePath}} }
            """;

        const string yaml = """
            openapi: 3.0.3
            info: { title: T, version: '1' }
            paths:
              /ping:
                get:
                  operationId: ping
                  responses:
                    '200':
                      description: ok
                      content:
                        application/json:
                          schema:
                            $ref: '#/components/schemas/Pong'
            components:
              schemas:
                Pong:
                  type: object
                  properties:
                    ok: { type: boolean }
            """;

        using var fromJson = CliHarness.RunText(json, ".json");
        using var fromYaml = CliHarness.RunText(yaml, ".yaml");

        Assert.Equal(0, fromJson.ExitCode);
        Assert.Equal(0, fromYaml.ExitCode);
        Assert.Equal(fromJson.Read("Models.g.cs"), fromYaml.Read("Models.g.cs"));
    }

    /// <summary>报告是可解析的 JSON，且带上来源信息：生成器版本、文档版本 / 标题、形态与分组。</summary>
    [Fact]
    public void Report_IsValidJsonWithProvenance()
    {
        using var run = CliHarness.Run("mini-3.1.yaml");

        using var report = JsonDocument.Parse(run.Read(CliRun.ReportName));
        var root = report.RootElement;
        Assert.StartsWith("axiosnet-gen/", root.GetProperty("generator").GetString());
        Assert.Equal("3.1.0", root.GetProperty("openApiVersion").GetString());
        Assert.Equal("Mini YAML", root.GetProperty("title").GetString());
        Assert.Equal("model", root.GetProperty("style").GetString());
        Assert.Equal("DiagnosticsRequests", root.GetProperty("groups")[0].GetProperty("name").GetString());
    }

    /// <summary>路径 / 头参数为非标量或非 simple 风格时，整个操作跳过并留告警，其余操作照常生成。</summary>
    [Fact]
    public void NonScalarPathAndHeaderParameters_SkipOperation()
    {
        // [Path] / [Header] 只接受标量：数组、对象、非 simple 风格都记警告并跳过整个操作。
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "T", "version": "1" },
              "paths": {
                "/array/{ids}": {
                  "get": {
                    "operationId": "byIds",
                    "parameters": [{ "name": "ids", "in": "path", "required": true,
                                     "schema": { "type": "array", "items": { "type": "integer" } } }],
                    "responses": { "200": { "description": "ok" } }
                  }
                },
                "/object/{meta}": {
                  "get": {
                    "operationId": "byObject",
                    "parameters": [{ "name": "meta", "in": "path", "required": true,
                                     "schema": { "type": "object", "properties": { "k": { "type": "string" } } } }],
                    "responses": { "200": { "description": "ok" } }
                  }
                },
                "/label/{id}": {
                  "get": {
                    "operationId": "byLabel",
                    "parameters": [{ "name": "id", "in": "path", "required": true, "style": "label",
                                     "schema": { "type": "string" } }],
                    "responses": { "200": { "description": "ok" } }
                  }
                },
                "/header-array": {
                  "get": {
                    "operationId": "headerArray",
                    "parameters": [{ "name": "X-Ids", "in": "header",
                                     "schema": { "type": "array", "items": { "type": "integer" } } }],
                    "responses": { "200": { "description": "ok" } }
                  }
                },
                "/ok": {
                  "get": { "operationId": "ok", "responses": { "200": { "description": "ok" } } }
                }
              }
            }
            """;

        using var run = CliHarness.RunText(spec, ".json", "--style", "interface");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(1, run.Operations);
        Assert.Contains("ARRAY_PATH_PARAMETER", run.Codes);
        Assert.Contains("NON_SCALAR_PATH_PARAMETER", run.Codes);
        Assert.Contains("UNSUPPORTED_PATH_STYLE", run.Codes);
        Assert.Contains("ARRAY_HEADER_PARAMETER", run.Codes);
        Assert.Contains("OPERATION_SKIPPED", run.Codes);

        var api = run.Read("IDefaultApi.g.cs");
        Assert.Contains("OkAsync(", api);
        Assert.DoesNotContain("ByIdsAsync", api);
        Assert.DoesNotContain("ByObjectAsync", api);
        Assert.DoesNotContain("ByLabelAsync", api);
        Assert.DoesNotContain("HeaderArrayAsync", api);

        // 被跳过的操作不能留下只被它引用的模型：对象路径参数若先映射再判定，会生成 ByObjectMeta 之类死类型。
        Assert.False(run.Exists("Models.g.cs"));
    }

    /// <summary>
    /// 路径模板占位符必须与运行时规则一致：重复占位符、含空格 / 逗号的花括号都在读取阶段拦下，
    /// 记 INVALID_PATH_TEMPLATE 并跳过该操作，其余操作照常生成。
    /// </summary>
    [Fact]
    public void InvalidPathTemplate_SkipsOperation()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "T", "version": "1" },
              "paths": {
                "/dup/{id}/{id}": {
                  "get": {
                    "operationId": "dup",
                    "parameters": [{ "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }],
                    "responses": { "200": { "description": "ok" } }
                  }
                },
                "/spaced/{ bad }": {
                  "get": {
                    "operationId": "spaced",
                    "parameters": [{ "name": "bad", "in": "path", "required": true, "schema": { "type": "string" } }],
                    "responses": { "200": { "description": "ok" } }
                  }
                },
                "/ok": { "get": { "operationId": "ok", "responses": { "200": { "description": "ok" } } } }
              }
            }
            """;

        using var run = CliHarness.RunText(spec, ".json", "--style", "interface");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(1, run.Operations);
        Assert.Contains("INVALID_PATH_TEMPLATE", run.Codes);
        Assert.Contains("OPERATION_SKIPPED", run.Codes);

        var api = run.Read("IDefaultApi.g.cs");
        Assert.Contains("OkAsync(", api);
        Assert.DoesNotContain("DupAsync", api);
        Assert.DoesNotContain("SpacedAsync", api);
    }

    /// <summary>JSON 对象里的重复键必须报生成错误，而不是把原始 ArgumentException 漏给用户。</summary>
    [Fact]
    public void DuplicateJsonKeys_FailAsGenerationException()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "T", "version": "1" },
              "paths": { "/a": { "get": { "operationId": "a", "responses": { "200": { "description": "ok" } } } } },
              "paths": { "/b": { "get": { "operationId": "b", "responses": { "200": { "description": "ok" } } } } }
            }
            """;

        Assert.Throws<GenerationException>(() => CliHarness.RunText(spec, ".json"));
    }

    /// <summary>
    /// application/vnd.api+json 等结构化 JSON 媒体类型仍按 JSON 序列化，但发送的 Content-Type 必须按声明，
    /// 因此接口式要落 [BodyContentType]、模型式要落 config.ContentType。
    /// </summary>
    [Fact]
    public void StructuredJsonMediaType_EmitsBodyContentType()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "T", "version": "1" },
              "paths": {
                "/upload": {
                  "post": {
                    "operationId": "upload",
                    "requestBody": {
                      "required": true,
                      "content": {
                        "application/vnd.api+json": {
                          "schema": { "type": "object", "properties": { "name": { "type": "string" } } }
                        }
                      }
                    },
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        using var api = CliHarness.RunText(spec, ".json", "--style", "interface");
        Assert.Contains("[BodyContentType(\"application/vnd.api+json\")]", api.Read("IDefaultApi.g.cs"));

        using var model = CliHarness.RunText(spec, ".json");
        Assert.Contains("config.ContentType = \"application/vnd.api+json\";", model.Read("DefaultRequests.g.cs"));
    }

    /// <summary>标题里的 U+2028 / U+0085 / 控制字符是 C# 行终止符，写进字符串字面量前必须转义成 \uXXXX。</summary>
    [Fact]
    public void EscapeString_EncodesCSharpLineTerminators()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "T\u2028X\u0085Y", "version": "1" },
              "paths": {
                "/ping": {
                  "get": {
                    "operationId": "ping",
                    "responses": {
                      "200": {
                        "description": "ok",
                        "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Pong" } } }
                      }
                    }
                  }
                }
              },
              "components": { "schemas": { "Pong": { "type": "object", "properties": { "ok": { "type": "boolean" } } } } }
            }
            """;

        using var run = CliHarness.RunText(spec, ".json");
        Assert.Equal(0, run.ExitCode);

        var header = run.Read("Models.g.cs");
        Assert.Contains("\\u2028", header);
        Assert.Contains("\\u0085", header);
        Assert.DoesNotContain("\u2028", header);
        Assert.DoesNotContain("\u0085", header);
    }

    /// <summary>无 type 的整数枚举落成 int（尊重 --integer-type long），含非整数时才退回 double 并记警告。</summary>
    [Fact]
    public void NumericEnum_IntegralValuesMapToInteger()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "T", "version": "1" },
              "paths": {
                "/ping": {
                  "get": {
                    "operationId": "ping",
                    "responses": {
                      "200": {
                        "description": "ok",
                        "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Payload" } } }
                      }
                    }
                  }
                }
              },
              "components": {
                "schemas": {
                  "Payload": {
                    "type": "object",
                    "properties": {
                      "code": { "enum": [1, 2, 3] },
                      "ratio": { "enum": [1.5, 2] }
                    }
                  }
                }
              }
            }
            """;

        using var run = CliHarness.RunText(spec, ".json");

        var models = run.Read("Models.g.cs");
        Assert.Contains("public int? Code { get; set; }", models);
        Assert.Contains("public double? Ratio { get; set; }", models);
        Assert.Contains("NUMERIC_ENUM_FRACTION", run.Codes);
    }

    /// <summary>deepObject 对象查询参数无法展开成多个键：只加 UNSUPPORTED_QUERY_STYLE 警告，不改生成形态。</summary>
    [Fact]
    public void DeepObjectQuery_WarnsWithoutChangingShape()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "T", "version": "1" },
              "paths": {
                "/search": {
                  "get": {
                    "operationId": "search",
                    "parameters": [{
                      "name": "filter", "in": "query", "style": "deepObject", "explode": true,
                      "schema": { "type": "object", "properties": { "name": { "type": "string" } } }
                    }],
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        using var run = CliHarness.RunText(spec, ".json", "--style", "interface");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("UNSUPPORTED_QUERY_STYLE", run.Codes);
        Assert.Contains("[Query] SearchAsyncFilter? filter", run.Read("IDefaultApi.g.cs"));
    }

    /// <summary>
    /// 枚举值的 Pascal 名恰好等于常量类名时会触发 CS0542，生成端必须补 Value 后缀回避；
    /// 其余值照常生成常量。
    /// </summary>
    [Fact]
    public void EnumConstants_AvoidMemberNamedLikeContainerClass()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "T", "version": "1" },
              "paths": {
                "/flag": {
                  "get": {
                    "operationId": "flag",
                    "responses": {
                      "200": {
                        "description": "ok",
                        "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Status" } } }
                      }
                    }
                  }
                }
              },
              "components": {
                "schemas": { "Status": { "type": "string", "enum": ["status-values", "other"] } }
              }
            }
            """;

        using var run = CliHarness.RunText(spec, ".json");

        Assert.Equal(0, run.ExitCode);
        var models = run.Read("Models.g.cs");
        Assert.Contains("public static class StatusValues", models);
        Assert.Contains("public const string StatusValuesValue = \"status-values\";", models);
        Assert.Contains("public const string Other = \"other\";", models);
    }

    /// <summary>请求类名撞运行库 ApiRequest&lt;T&gt; 时必须自动改名，否则生成的类会遮蔽泛型基类、无法编译。</summary>
    [Fact]
    public void RequestClass_AvoidsReservedRuntimeTypeName()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "T", "version": "1" },
              "paths": {
                "/api": {
                  "get": {
                    "operationId": "api",
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        using var run = CliHarness.RunText(spec, ".json");

        Assert.Equal(0, run.ExitCode);
        var requests = run.Read("DefaultRequests.g.cs");
        Assert.Contains("public sealed class ApiRequest2 : ApiRequest<", requests);
        Assert.DoesNotContain("public sealed class ApiRequest :", requests);
    }

    /// <summary>
    /// multipart 请求体声明 additionalProperties 时，未知字段无法展开成部件：必须留警告，
    /// 不能静默丢弃（调用方会以为额外字段已经覆盖）。
    /// </summary>
    [Fact]
    public void MultipartAdditionalProperties_IsReported()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "T", "version": "1" },
              "paths": {
                "/upload": {
                  "post": {
                    "operationId": "upload",
                    "requestBody": {
                      "required": true,
                      "content": {
                        "multipart/form-data": {
                          "schema": {
                            "type": "object",
                            "required": ["file"],
                            "properties": { "file": { "type": "string", "format": "binary" } },
                            "additionalProperties": true
                          }
                        }
                      }
                    },
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        using var run = CliHarness.RunText(spec, ".json", "--style", "interface");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("MULTIPART_ADDITIONAL_PROPERTIES", run.Codes);
        Assert.Contains("[Part] byte[] file", run.Read("IDefaultApi.g.cs"));
    }

    /// <summary>YAML 的流式写法也以 <c>{</c> 开头：JSON 解析失败后必须回退 YAML，而不是直接报「不是合法的 JSON」。</summary>
    [Fact]
    public void FlowStyleYaml_IsAccepted()
    {
        const string spec = """
            { openapi: "3.0.3", info: { title: "T", version: "1" }, paths: { "/ping": { get: { operationId: "ping", responses: { "200": { description: "ok" } } } } } }
            """;

        using var run = CliHarness.RunText(spec, ".yaml");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("public sealed class PingRequest : ApiRequest<", run.Read("DefaultRequests.g.cs"));
    }

    /// <summary>只有 http bearer / basic 能映射成 [Authorize]，apiKey 与 oauth2 只记告警不生成声明。</summary>
    [Fact]
    public void SecuritySchemes_OnlyHttpBearerAndBasicEmitAuthorize()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "T", "version": "1" },
              "paths": {
                "/bearer": { "get": { "operationId": "bearer", "security": [{ "bearerAuth": [] }],
                                      "responses": { "200": { "description": "ok" } } } },
                "/basic": { "get": { "operationId": "basic", "security": [{ "basicAuth": [] }],
                                     "responses": { "200": { "description": "ok" } } } },
                "/key": { "get": { "operationId": "key", "security": [{ "keyAuth": [] }],
                                   "responses": { "200": { "description": "ok" } } } },
                "/oauth": { "get": { "operationId": "oauth", "security": [{ "oauth": [] }],
                                     "responses": { "200": { "description": "ok" } } } }
              },
              "components": {
                "securitySchemes": {
                  "bearerAuth": { "type": "http", "scheme": "bearer" },
                  "basicAuth": { "type": "http", "scheme": "basic" },
                  "keyAuth": { "type": "apiKey", "in": "query", "name": "api_key" },
                  "oauth": { "type": "oauth2" }
                }
              }
            }
            """;

        using var run = CliHarness.RunText(spec, ".json", "--style", "interface");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("APIKEY_SECURITY", run.Codes);
        Assert.Contains("OAUTH_SECURITY", run.Codes);

        var api = run.Read("IDefaultApi.g.cs");
        // Bearer 与 Basic 两条生成授权声明；apiKey / oauth2 只留警告。
        Assert.Equal(2, api.Split("[Authorize").Length - 1);
        Assert.Contains("[Authorize(\"Basic\")]", api);
    }

    /// <summary>text/* 请求体必须保留文档声明的媒体类型：text/csv 不能一律改写成 text/plain。</summary>
    [Fact]
    public void TextMediaType_IsPreserved()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "T", "version": "1" },
              "paths": {
                "/import": {
                  "post": {
                    "operationId": "importCsv",
                    "requestBody": {
                      "required": true,
                      "content": { "text/csv": { "schema": { "type": "string" } } }
                    },
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        using var run = CliHarness.RunText(spec, ".json", "--style", "interface");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("[BodyContentType(\"text/csv\")]", run.Read("IDefaultApi.g.cs"));
    }

    /// <summary>多个 2xx 时优先选带正文的那个：200 无 content、201 才有 schema，不能按无返回值生成。</summary>
    [Fact]
    public void MultipleSuccessResponses_PreferResponseWithContent()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "T", "version": "1" },
              "paths": {
                "/ping": {
                  "post": {
                    "operationId": "ping",
                    "responses": {
                      "200": { "description": "ok" },
                      "201": {
                        "description": "created",
                        "content": {
                          "application/json": {
                            "schema": { "type": "object", "properties": { "ok": { "type": "boolean" } } }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """;

        using var run = CliHarness.RunText(spec, ".json");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("class PingRequest : ApiRequest<PingAsyncResponse>", run.Read("DefaultRequests.g.cs"));
        Assert.Contains("MULTIPLE_SUCCESS_RESPONSES", run.Codes);
    }

    /// <summary>两个 tag 只差大小写时，请求模型文件名在 Windows 上会互相覆盖，必须继续去重。</summary>
    [Fact]
    public void TagNamesDifferingOnlyByCase_GetDistinctFiles()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "T", "version": "1" },
              "paths": {
                "/a": { "get": { "operationId": "a", "tags": ["AB"], "responses": { "200": { "description": "ok" } } } },
                "/b": { "get": { "operationId": "b", "tags": ["Ab"], "responses": { "200": { "description": "ok" } } } }
              }
            }
            """;

        using var run = CliHarness.RunText(spec, ".json");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("ABRequests.g.cs", run.FileNames);
        Assert.Contains("AbRequests2.g.cs", run.FileNames);
    }

    /// <summary>只有空白字符的 tag 不能建出 IValueApi 分组，应回落到 Default。</summary>
    [Fact]
    public void BlankTagFallsBackToDefaultGroup()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "T", "version": "1" },
              "paths": {
                "/ping": { "get": { "operationId": "ping", "tags": [" "], "responses": { "200": { "description": "ok" } } } }
              }
            }
            """;

        using var run = CliHarness.RunText(spec, ".json");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("DefaultRequests.g.cs", run.FileNames);
        Assert.DoesNotContain("ValueRequests.g.cs", run.FileNames);
    }

    /// <summary>枚举值恰好与枚举类型同名时必须改名（CS0542），线上值不受影响。</summary>
    [Fact]
    public void EnumMemberNamedLikeEnumType_IsRenamed()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "T", "version": "1" },
              "paths": {
                "/ping": {
                  "get": {
                    "operationId": "ping",
                    "responses": {
                      "200": {
                        "description": "ok",
                        "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Status" } } }
                      }
                    }
                  }
                }
              },
              "components": {
                "schemas": { "Status": { "type": "string", "enum": ["Status", "Other"] } }
              }
            }
            """;

        using var run = CliHarness.RunText(spec, ".json");
        var models = run.Read("Models.g.cs");

        Assert.Contains("public enum Status", models);
        Assert.Contains("StatusValue", models);
    }

    /// <summary>value__ 是枚举保留成员名（CS0076），含此值时退回常量类。</summary>
    [Fact]
    public void ReservedEnumMemberValue__FallsBackToConstants()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "T", "version": "1" },
              "paths": {
                "/ping": {
                  "get": {
                    "operationId": "ping",
                    "responses": {
                      "200": {
                        "description": "ok",
                        "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Weird" } } }
                      }
                    }
                  }
                }
              },
              "components": {
                "schemas": { "Weird": { "type": "string", "enum": ["value__", "ok"] } }
              }
            }
            """;

        using var run = CliHarness.RunText(spec, ".json");
        var models = run.Read("Models.g.cs");

        Assert.Contains("public static class WeirdValues", models);
        Assert.DoesNotContain("public enum Weird", models);
    }

    /// <summary>同一个 security 方案组里的多个 scheme（AND 语义）只能取一个，必须留下告警。</summary>
    [Fact]
    public void AndSecuritySchemes_WarnAboutDroppedSchemes()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "T", "version": "1" },
              "paths": {
                "/ping": {
                  "get": {
                    "operationId": "ping",
                    "security": [{ "bearerAuth": [], "keyAuth": [] }],
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              },
              "components": {
                "securitySchemes": {
                  "bearerAuth": { "type": "http", "scheme": "bearer" },
                  "keyAuth": { "type": "apiKey", "in": "query", "name": "api_key" }
                }
              }
            }
            """;

        using var run = CliHarness.RunText(spec, ".json");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("MULTI_SCHEME_SECURITY", run.Codes);
        Assert.Contains("bearerAuth + keyAuth", run.MessageFor("MULTI_SCHEME_SECURITY")!);
    }

    /// <summary>类名本身是 AdditionalProperties 时，兜底属性不能叫同一个名字（CS0542）。</summary>
    [Fact]
    public void ExtensionDataProperty_AvoidsClassNamedAdditionalProperties()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "T", "version": "1" },
              "paths": {
                "/ping": {
                  "get": {
                    "operationId": "ping",
                    "responses": {
                      "200": {
                        "description": "ok",
                        "content": {
                          "application/json": {
                            "schema": { "$ref": "#/components/schemas/AdditionalProperties" }
                          }
                        }
                      }
                    }
                  }
                }
              },
              "components": {
                "schemas": {
                  "AdditionalProperties": {
                    "type": "object",
                    "properties": { "kind": { "type": "string" } },
                    "additionalProperties": true
                  }
                }
              }
            }
            """;

        using var run = CliHarness.RunText(spec, ".json");
        var models = run.Read("Models.g.cs");

        Assert.Contains("public sealed class AdditionalProperties", models);
        Assert.Contains("AdditionalPropertiesValue", models);
    }

    /// <summary>JSON Pointer 里的数组下标必须能解析（如 #/paths/~1a/get/parameters/0），越界返回 null。</summary>
    [Fact]
    public void JsonPointerArrayIndex_IsResolved()
    {
        const string spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "T", "version": "1" },
              "paths": {
                "/a": {
                  "get": {
                    "operationId": "ping",
                    "parameters": [{ "name": "id", "in": "query", "schema": { "type": "string" } }],
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        var root = SpecParser.Parse(spec, "inline");
        var resolver = new RefResolver(root, new WarningCollector());

        var parameter = resolver.ResolvePointer("#/paths/~1a/get/parameters/0");
        Assert.NotNull(parameter);
        Assert.Equal("id", parameter!["name"]?.AsString());

        Assert.Null(resolver.ResolvePointer("#/paths/~1a/get/parameters/1"));
        Assert.Null(resolver.ResolvePointer("#/paths/~1a/get/parameters/not-a-number"));
    }
}
