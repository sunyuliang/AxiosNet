# AxiosNet

[![.NET](https://img.shields.io/badge/.NET-6%2B%20%7C%20Framework%204.7.2%2B-512BD4?logo=.net)](https://dotnet.microsoft.com/)
[![netstandard](https://img.shields.io/badge/.NET%20Standard-2.0-512BD4)](https://learn.microsoft.com/dotnet/standard/net-standard)
[![nuget](https://img.shields.io/badge/nuget-AxiosNet?logo=nuget)](https://www.nuget.org/packages/AxiosNet)
[![tests net8.0](https://img.shields.io/badge/tests%20net8.0-2694%20passed-success)](#构建与测试)
[![tests net6.0](https://img.shields.io/badge/tests%20net6.0-2562%20passed-success)](#构建与测试)
[![coverage](https://img.shields.io/badge/coverage-92.1%25_(branch_87.6%25)-success)](#构建与测试)
[![license](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

**简体中文** | [English](README.en.md)

axios 风格的 .NET HTTP 客户端。写法对齐 JS [axios](https://github.com/axios/axios)，并补齐服务端真正需要的部分：按 origin 复用的连接池、重试、熔断、并发对冲、备用端点、流式响应、在途并发闸门、声明式 API 客户端（模型式 / 接口式 / OpenAPI 导入）、OpenTelemetry 观测点。

| 项 | 值 |
| --- | --- |
| 包 | `AxiosNet`（核心 + 声明式客户端同包）、`AxiosNet.DependencyInjection`（可选）、`AxiosNet.Cli`（可选，设计期 dotnet tool） |
| 目标框架 | 三目标 `netstandard2.0` / `net6.0` / `net8.0`：.NET 8+ 取 `lib/net8.0`，.NET 6 / 7 取 `lib/net6.0`，.NET Framework 4.7.2+ / .NET Core 2.0+ / .NET 5 取 `lib/netstandard2.0`（[兼容资产](#net-standard-20-兼容资产)） |
| 依赖 | 零第三方；`net6.0` / `net8.0` 只用 BCL，`netstandard2.0` 兼容资产另带 9 个微软第一方 BCL 兼容包（NuGet 自动带入） |
| 风格 | 全异步：`Task` + `CancellationToken` |
| 观测 | trace / metrics / events 默认开启，无人订阅时零产出 |

> 仓库：[Gitee（itsm/axios-net）](https://gitee.com/itsm/axios-net) 是提交主仓，PR / tag 以 Gitee 为准；[GitHub](https://github.com/sunyuliang/AxiosNet) 为只读镜像。

---

## 目录

| 分组 | 章节 |
| --- | --- |
| 入门 | [安装](#安装) · [快速上手](#快速上手) · [常用任务](#常用任务) |
| 请求与响应 | [请求体与 ContentType（JSON 默认）](#请求体与-contenttypejson-默认) · [查询参数 params](#查询参数-params) · [表单与文件上传](#表单与文件上传) · [流式请求体](#流式请求体) · [响应类型与下载](#响应类型与下载) · [流式响应](#流式响应) · [请求体压缩](#请求体压缩) · [超时与取消](#超时与取消) · [进度、限速与大小上限](#进度限速与大小上限) · [发送前钩子（签名 / 最终请求）](#发送前钩子签名--最终请求) · [原始响应字节](#原始响应字节) · [方法与 URL](#方法与-url) · [在途并发上限](#在途并发上限) |
| 错误与配置 | [错误处理](#错误处理) · [配置](#配置)（[配置速查](#配置速查) / [客户端](#客户端) / [拦截器](#拦截器) / [数据转换与序列化](#数据转换与序列化) / [连接与传输](#连接与传输) / [可观测性](#可观测性)） |
| 治理与缓存 | [治理](#治理)（[重试](#重试) / [熔断](#熔断) / [并发对冲](#并发对冲) / [备用端点](#备用端点) / [幂等键](#幂等键) / [尝试管线](#尝试管线) / [短路响应](#短路响应)） · [缓存](#缓存) |
| 声明式 | [声明式客户端](#声明式客户端)（[模型式](#模型式-api) / [接口式](#接口式-api) / [值格式化](#值格式化) / [OpenAPI 导入](#从-openapi-导入) / [凭据与令牌管理](#凭据与令牌管理)） |
| 运行时与参考 | [运行时与 AOT](#运行时与-aot) · [.NET Standard 2.0 兼容资产](#net-standard-20-兼容资产) · [参考](#参考)（[能力地图](#能力地图) / [与 axios 对照](#与-axios-对照) / [依赖注入](#依赖注入) / [示例项目](#示例项目) / [构建与测试](#构建与测试) / [项目结构](#项目结构)） |
| 其它 | [License](#license) |

按目标选入口：

| 想做什么 | 从哪读 |
| --- | --- |
| 第一次使用 | [安装](#安装) → [快速上手](#快速上手) → [能力地图](#能力地图) |
| 在 .NET Framework / .NET Core 2.x / 3.x 上使用 | [.NET Standard 2.0 兼容资产](#net-standard-20-兼容资产) |
| 从 axios 迁移 | [与 axios 对照](#与-axios-对照) → 有意差异 → [快速上手](#快速上手) |
| 只查某个选项 | [配置速查](#配置速查) |
| 做服务端治理 | [治理](#治理) |
| 排查线上问题 | [错误处理](#错误处理) → [可观测性](#可观测性) |

## 安装

> 从 1.x 升级：字段改名与行为收紧见 [1.5 破坏性变更](docs/breaking-changes-1.5.md)。

> 运行时基线：现代资产覆盖 .NET 6 及以上；兼容资产覆盖 .NET Framework 4.7.2+、.NET Core 2.0+（含 3.1）与 .NET 5。NuGet 自动选资产，无需任何配置。.NET Core 1.0 / 1.1 只支持到 `netstandard1.6`，不在支持范围内；能力边界见 [.NET Standard 2.0 兼容资产](#net-standard-20-兼容资产)。

```bash
dotnet add package AxiosNet                       # 核心客户端 + 声明式 API 客户端（同一包）
dotnet add package AxiosNet.DependencyInjection   # 可选：DI 注册扩展

dotnet tool install -g AxiosNet.Cli               # 可选：从 OpenAPI 生成客户端（设计期工具，不进运行时）
```

引用本仓库源码：`dotnet add your-app.csproj reference src/AxiosNet/AxiosNet.csproj`

## 快速上手

原生一行代码直接调用：

```csharp

using AxiosNet;

//字符串形式：返回完整响应（Status / Headers / Data）
var rsp=await Axios.GetAsync<string>("https://api.example.com/users/1");

//动态对象形式：Data 的运行时类型是 AxiosDynamic
var dyUser=await Axios.GetAsync<dynamic>("https://api.example.com/users/1");

//强类型形式：Data 已反序列化为 User
var user=(await Axios.GetAsync<User>("https://api.example.com/users/1")).Data!;

```

端点成组时，声明式有两种并列写法，**模型式是默认推荐**：把请求写成模型直接传，或把端点写成接口交给 `Create<T>()`。两条路径共用同一个客户端与同一套绑定 / 授权 / 校验规则（详见[模型式 API](#模型式-api) / [接口式 API](#接口式-api)）。

**模型式（默认推荐）**

```csharp
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;

[Get("users/{id}")]
public sealed class GetUserRequest : IApiRequest<User>
{
    [Path] public int Id { get; set; }
}

var client = new ApiClient("https://api.example.com/");
var user = (await client.RequestAsync(new GetUserRequest { Id = 5 })).Data!;   // User
```

**接口式**

```csharp
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;

public interface IUserApi
{
    [Get("users/{id}")]
    Task<AxiosResponse<User>> GetAsync([Path] int id, CancellationToken cancellationToken = default);
}

var api = client.Create<IUserApi>();
var user = (await api.GetAsync(5)).Data!;                                      // User
```

扩展入口与 `ApiClient` 共用同一个门面（按 `AxiosClient` 缓存）：先 `new ApiClient(client)` 配好 `CredentialProvider` / `ErrorFactory`，再走 `client.Create<T>()` / `RequestAsync` / `RequestStreamAsync`，这些状态照常生效，不会静默变成另一个匿名门面。

五个概念：

| 概念 | 类型 | 职责 |
| --- | --- | --- |
| Client | `Axios` / `AxiosClient` | 进程默认实例 + 实例客户端，同时是声明式入口 |
| Request | `AxiosConfig` | 一次请求，顶层叶子 |
| Response | `AxiosResponse<T>` | 状态码、响应头与已解析的正文 |
| Error | `AxiosException` | 所有失败的统一出口，带稳定 `Code` |
| Endpoint | `IApiRequest<T>` / 接口 | 可选扩展概念：把端点写成模型或接口 |

返回形态由泛型参数决定：

| 写法 | `Data` 的类型 | 适合 |
| --- | --- | --- |
| `GetAsync<string>` | `string`（原文，不解析） | 纯文本接口、调试、自己解析 |
| `GetAsync<dynamic>` | `AxiosDynamic`（成员 / 索引 / 缺省字段返回 `null`） | 字段不固定、快速验证 |
| `GetAsync<T>` | `T` | 生产代码 |
| `GetAsync()`（非泛型） | `JsonElement`（非法 JSON 回退原文，二进制给 `byte[]`） | 只看结构、不建类型 |

下一步：按能力浏览看[常用任务](#常用任务)，逐项配置看[配置](#配置)；端点成组时看[声明式客户端](#声明式客户端)。

## 常用任务

按能力浏览；每个小节只讲一种请求形态，配置项见[配置速查](#配置速查)。

### 请求体与 ContentType（JSON 默认）

**编码格式只由 `ContentType` 决定，与 `data` 的运行时类型无关**（默认 `application/json`）。

| `ContentType` | 对象 / 字典 / dynamic / 集合 | 基础类型（数值 / 布尔 / 枚举 / Guid / 日期…） | 原始内容（`string` / `byte[]` / `Memory<byte>` / `Stream` / `HttpContent` / 字节块异步序列） |
| --- | --- | --- | --- |
| `application/json`（默认） | 当前序列化器输出 JSON | 原值文本（`42`、`true`、`Paid`） | 原样发送 |
| `application/x-www-form-urlencoded` | 键值对表单（`a[b]=v`、`a[]=v`、null 跳过） | 忽略（空正文） | 原样发送 |
| `multipart/form-data` | 字段 + 文件部件 | 抛 `ERR_BAD_OPTION_VALUE`（放进对象字段） | 抛 `ERR_BAD_OPTION_VALUE`（`HttpContent` 原样透传） |
| 其他（`application/xml` / `yaml`…） | 交给当前序列化器 | 原值文本 | 原样发送 |

```csharp
await http.PostAsync("users", new { name = "bob" });                    // JSON

await http.PostAsync("login", new Dictionary<string, object?>          // 表单
{
    ["username"] = "alice",
    ["tags"] = new[] { "a", "b" }                                       // -> tags[]=a&tags[]=b
}, c => c.ContentType = AxiosContentType.FormUrlEncoded);

await http.PostAsync("bin", bytes, c => c.ContentType = AxiosContentType.OctetStream);   // 二进制：显式声明

await http.GetAsync("reports/query", new { from = "2024-01-01" });      // 任意方法都能带 body
await http.DeleteAsync("users/1", new { reason = "duplicated" });
```

常用常量：`AxiosContentType.Json` / `FormUrlEncoded` / `MultipartFormData` / `OctetStream` / `Xml` / `JsonPatch` / `ProblemJson` / `TextPlain` / `TextHtml`；也可直接写带参字符串：`"multipart/form-data; boundary=----axios-net"`。

### 查询参数 params

`Params` 收匿名对象、集合，也能直接给已编码好的查询串；展平与转义规则见下。

```csharp
await http.GetAsync("search", c => c.Params = new
{
    q = "a b",                        // q=a%20b
    ids = new[] { 1, 2 },             // ids[]=1&ids[]=2
    filter = new { kind = "book" }    // filter[kind]=book
});

c.Params = "a=1&b=2";                 // 已序列化好的查询串，原样透传
c.Params = new[] { 1, 2 };            // 根集合：[0]=1&[1]=2
```

- 集合形态由 `Serialization.CollectionFormat` 决定：`Brackets`（默认）/ `Multi` / `Csv` / `Ssv` / `Tsv` / `Pipes`。
- 键与值走 `Uri.EscapeDataString`（空格 `%20`）；手写在 `Url` 里的查询串不二次编码。
- `Params` 传标量抛 `ERR_BAD_OPTION_VALUE`（没有键名）。完全自定义：`c.Serialization.QueryEncoder = p => "a=1&b=2"`（设置 `Serialization.ValueText` 时，回调里看到的仍是定型后的文本，内部封套已剥离）。

「值 → 文本」在 `Params` / `[Path]` / `[Header]` / 表单 / multipart / 裸标量正文之间共用同一条路径：

| 值 | 文本形态 |
| --- | --- |
| `bool` | `true` / `false` |
| 枚举 | 名字 |
| `DateTime` / `DateTimeOffset` | UTC 毫秒 ISO（`Unspecified` 按 UTC） |
| 同上，但作为裸标量正文 | 往返格式 `"O"` |
| `DateOnly` | `yyyy-MM-dd` |
| `byte[]` | base64 |
| 其它 | `IFormattable` + `InvariantCulture` |

按成员改写形态用 `[Format]`，按实例改写的钩子是 `AxiosConfig.Serialization.ValueText`（回调的第二个参数拿到值所在通道，见[值格式化](#值格式化)）。

### 表单与文件上传

三种写法：`PostFormAsync` 走 multipart、裸流直传、自己拼 `MultipartFormDataContent`。

```csharp
// 1) multipart：字段 + 文件（FormFile / Stream / byte[] / HttpContent 都行）
await http.PostFormAsync("upload", new
{
    title = "季度报告",
    files = new[]
    {
        new FormFile("a.pdf", File.OpenRead("a.pdf"), "application/pdf"),  // 流随请求释放
        FormFile.FromPath("b.zip"),                                        // 库负责打开 / 释放
        FormFile.FromBytes("c.txt", bytes, "text/plain")
    }
});

// 2) 裸流直传（大文件首选，内存占用恒定）
await using var source = File.OpenRead(@"D:\data\report.bin");
await http.PostAsync<UploadResult>("api/files/raw", source, c =>
{
    c.ContentType = "application/octet-stream";
    c.Headers.Set("X-File-Name", "report.bin");     // 裸流没有文件名，用头带过去
});

// 3) 自己拼 MultipartFormDataContent
using var multipart = new MultipartFormDataContent();
multipart.Add(new StringContent("alice"), "name");
await http.PostAsync("upload", multipart);          // 原样透传，保留自带头与边界
```

- 字段名取属性名 / 字典键（识别 `[JsonPropertyName]` / `[JsonIgnore]`），嵌套写 `meta[author]`，集合展开成同名重复部件；规则可用 `Serialization.FormFieldName` 改。
- 根节点是文件数组时没有键名，用 `FormFile.FieldName` 指定。
- 部件随请求释放（成功 / 异常 / 取消都 `Dispose`）；要复用请先读成 `byte[]`。
- 同一个 `FormFile` / `Stream` 只能挂一个部件：流是单向读的，第一个部件读完后游标在末尾，第二个只会发出 0 字节，装配时直接报 `ERR_BAD_OPTION_VALUE`。
- 字段名 / 文件名含 `"`、`\`、CR/LF、控制字符会破坏 `Content-Disposition`，发出前直接报错。
- ASP.NET Core 项目里 `FormFile` 有歧义：`using ClientFile = AxiosNet.FormFile;`。

### 流式请求体

正文由调用方边算边给、库不缓冲：`IAsyncEnumerable<byte[]>` 或 `IAsyncEnumerable<ReadOnlyMemory<byte>>` 直接作为 `data`。

```csharp
// 逐块产出（这里从流里读；数据库游标、压缩管道、日志导出同理）
static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunks(
    Stream source, [EnumeratorCancellation] CancellationToken token = default)
{
    var buffer = new byte[64 * 1024];
    int read;
    while ((read = await source.ReadAsync(buffer, token)) > 0)
    {
        yield return buffer.AsMemory(0, read);
    }
}

await using var file = File.OpenRead(@"D:\data\export.csv");
await http.PostAsync<UploadResult>("ingest", ReadChunks(file), c =>
{
    c.ContentType = "text/csv";          // 与其它原始内容一致：只应用 Content-Type
});
```

- **长度未知**：不预读、不算总长，传输层按 `Transfer-Encoding: chunked`（HTTP/1.1；默认策略下明文 `http://` 落在 1.1）或 HTTP/2 DATA 帧发送。因此 `Limits.MaxRequestBytes` 拦不住它——该上限只对能算出长度的正文生效。
- **一次性**：序列被消费后无法回拨，所以开启重试或备用端点时在**发出前**抛 `ERR_BAD_OPTION_VALUE`（不需要重发就关掉它们）。并发对冲不报错：它只在正文能安全并发重放（内存缓冲）时才生效，流式正文按单次发送处理。要可重放，请改用 `byte[]` 或可 seek 的流。
- **块大小由调用方定**：库把每块原样写出，不再切分或合并；64–256 KB 是常见甜点，过小的块会放大 chunked 框架自身开销。
- **只认字节块**：`IAsyncEnumerable<T>`（如 `IAsyncEnumerable<Order>`）不是正文，直接报 `ERR_BAD_OPTION_VALUE` 并给出指引；要发 JSON 数组请先写成 `Stream` 或 `byte[]`。
- **取消**：请求令牌会传给你的迭代器（签名里用 `[EnumeratorCancellation]` 接住），取消即停在上一次 `yield` 之后。
- **307 / 308 重定向**：默认跟随重定向，这两个状态码会把正文再发一遍——库会重新枚举一次序列，所以生产者必须能重启（迭代器重新 `GetAsyncEnumerator`，或自定义实现每次返回新枚举器）。一次性来源（例如直接转发一条已消费的网络流）请把客户端选项 `Transport.MaxRedirects` 设为 `0`（建池时等价于 `Transport.Pool.AllowAutoRedirect = false`）。
- **`PipeReader`**：net6 / net8 的基础框架不含 `System.IO.Pipelines`，库不为此引依赖；用 `pipe.Reader.AsStream()` 包成 `Stream` 传入即可（走不可 seek 流路径，同样一块一块写出）。

### 响应类型与下载

`ResponseType` 决定正文怎么交付，泛型参数会替你选好对应的一种。

```csharp
var typed  = await http.GetAsync<User>("users/1");                        // 强类型
var raw    = await http.GetAsync("users/1");                              // JsonElement
var text   = await http.GetAsync("ping", c => c.ResponseType = ResponseType.Text);
var bytes  = await http.GetAsync("file", c => c.ResponseType = ResponseType.ArrayBuffer);
var stream = await http.GetAsync("file", c => c.ResponseType = ResponseType.Stream);
var dyn    = await http.GetAsync<dynamic>("users/1");                     // 无需 DTO
```

`<Stream>` / `<byte[]>` / `<dynamic>` 会自动切到对应响应类型，不必手写 `ResponseType`。

下载：

```csharp
// 大文件：流式落盘，内存占用恒定
var response = await http.GetAsync<Stream>("files/report.zip");
await using (var source = response.Data!)
await using (var target = File.Create(@"D:\downloads\report.zip"))
{
    await source.CopyToAsync(target);
}

// 小文件
var bytes = (await http.GetAsync<byte[]>("files/icon.png")).Data!;
await File.WriteAllBytesAsync(@"D:\downloads\icon.png", bytes);
```

- 流**必须释放**：读完或释放后连接才回池；只读一半就释放，这条 TCP 连接会被丢弃。
- 下载期间该 origin 的租约保持占用，空闲驱逐不会打断长下载。
- 流式请求遇非 2xx 会立即丢弃响应体并归还连接（`ex.Response.Data` 为 `null`，`Status` / `Headers` 仍可用）。
- **错误里的响应上下文**：只要状态码与响应头已到手——5xx、反序列化失败、`Limits.MaxResponseBytes` 声明预检 / 段式超限、`ResponseTooLarge`、`OnRawResponse` / `OnDownloadProgress` 回调失败、短路正文超限——`AxiosException.Response` 都非 `null`。例外是流式消费期（`ReadAsync` 返回后调用方自己读流时触发的上限错误），这时没有上下文可补，`Response` 保持 `null`。

### 流式响应

`RequestStreamAsync` 把响应逐帧解析成 `IAsyncEnumerable<T>`：`await` 拿到状态码与响应头，帧在枚举时才读。

```csharp
var response = await client.RequestStreamAsync(
    new ChatStreamRequest { Prompt = "hi" },
    AxiosStreamFormat.ServerSentEvents);          // 不传格式 = NDJSON

await foreach (var chunk in response.Data!)
{
    Console.Write(chunk.Text);
}
```

| 格式 | 帧边界 | 典型来源 |
| --- | --- | --- |
| `NewlineDelimitedJson`（默认） | 一行一个 JSON 值，空行跳过 | 日志导出、逐行下发 |
| `ServerSentEvents` | 空行结束一帧，`data: [DONE]` 结束流 | LLM 增量输出、行情推送 |
| `JsonArray` | 顶层数组的每个元素 | 批量 / 分页列表 |

```csharp
var pages = await client.RequestStreamAsync(new ListOrdersRequest(), AxiosStreamFormat.JsonArray);
await foreach (var order in pages.Data!)
{
    Console.WriteLine(order.Id);                   // 元素收尾即交付，不等整个数组下完
}
```

- **超时**：正文与响应头共用同一份预算——流式响应的正文读取也计入 `Timeout`（`Resilience.TotalTimeout` 生效时是它的剩余额度），读到一半到点即 `ECONNABORTED`；不设超时时正文不限时，靠取消令牌退出。
- **单帧上限**：默认 8 MB，合法 1–64 MB（`Limits.MaxStreamFrameBytes`），越界发送前抛 `ERR_BAD_OPTION_VALUE`。没有「不限制」语义，峰值内存约为上限的 2–4 倍。上限统计的是**原始线上字节**（含字段前缀、行尾、分隔符），所以各格式实际可用 payload 会少于上限——NDJSON 与 JSON Array 少 1 B，SSE 每行少 7 B（LF）或 8 B（CRLF）。
- **编码**：NDJSON / SSE 要求 ASCII 兼容 charset，宽字符编码取流时报 `ERR_BAD_RESPONSE`；`JsonArray` 必须 UTF-8。UTF-8 BOM 会跳过。
- **SSE 空帧**：`:` 心跳与空 `data:` 一并跳过——单条空 `data:` 不产生事件；多条 `data:` 以 LF 连接，首条为空时不补前导 LF（SSE 在本库只用于 JSON 反序列化，空事件本来也无法交付）。
- **所有权**：流只能枚举一次；`await foreach` 结束或 `break` 自动释放，手动 `MoveNextAsync` 需自己 `DisposeAsync`。
- **显式 `ResponseType`**：流式入口在未显式设置 `ResponseType` 时自动切到 `Stream`；按请求或实例默认显式设成非 `Stream` 时，发出前抛 `ERR_BAD_OPTION_VALUE`，不静默覆盖调用方的选择。
- **错误**：状态码错误在 `await` 抛出；帧解析失败 `ERR_BAD_RESPONSE`，中途断流 `ERR_NETWORK`，取消 `ERR_CANCELED`。

### 请求体压缩

`Compression`（`Serialization` 之外的单次 / 实例项，默认 `null` = 不压缩）让库把正文压好再发，并自动写 `Content-Encoding`：

```csharp
await http.PostAsync("users", new { name = "bob" }, c => c.Compression = AxiosCompression.Gzip);

var client = new AxiosClient(new AxiosConfig { Compression = AxiosCompression.Brotli });   // 实例默认
await client.PostAsync(url, data, c => c.Compression = AxiosCompression.None);             // 单次关掉
```

- 算法：`Gzip` / `Deflate` / `Brotli`（`br`），压缩级别固定 `Fastest`——HTTP 的瓶颈通常在网络，省 CPU 比压到最小更划算。`Deflate` 发的是 RFC 1950 的 zlib 包装（RFC 7230 里的 `deflate` 指的就是它），不是裸 DEFLATE：Java / Tomcat 与多数网关只认 zlib。
- 只作用于**库按字节缓冲出来的正文**：对象 / 标量 / `string` / `byte[]` / `Memory<byte>` / 表单编码。压缩后 `Content-Length` 是压缩后长度，`Limits.MaxRequestBytes` 也按压缩后字节数校验。
- 拒绝（`ERR_BAD_OPTION_VALUE`）：`Stream` 与流式正文（没有完整长度可压，要压缩请先压好再传）、调用方自带的 `HttpContent`（压缩与编码归内容自己）、multipart（部件里可能含流式文件）、以及同时手写 `Content-Encoding` 头（与 `Compression` 二选一）。
- 服务端必须自己按 `Content-Encoding` 解压请求体（ASP.NET Core 等框架默认不解压请求体）；对端不支持时不要开启。

### 超时与取消

```csharp
await client.GetAsync("slow", c => c.Timeout = TimeSpan.FromMilliseconds(500));   // 单次
await client.GetAsync("forever", c => c.Timeout = TimeSpan.Zero);                 // 关掉实例默认超时

using var cts = new CancellationTokenSource();                                    // 等价 AbortController
var task = client.GetAsync("slow", cancellationToken: cts.Token);
cts.CancelAfter(TimeSpan.FromSeconds(1));
```

- `Timeout` 是**单次尝试**的预算（建连 + 等响应头 + 读正文），默认 `Zero` 表示不超时；上限约 49.7 天，超出收敛到上限。
- `Resilience.TotalTimeout` 是**整条逻辑请求**的预算（从调用开始，含拦截器、排队、重试等待、对冲分支）：`Timeout` 会被重试 / 备用端点 / 对冲各自复制一份，`Resilience.TotalTimeout` 只有一份，每次尝试实际拿到 `min(Timeout, 剩余)`。
- 超时抛 `ECONNABORTED`（消息可用 `Observability.TimeoutMessage` 覆盖），取消抛 `ERR_CANCELED`，原始 `OperationCanceledException` 留在 `Cause`；普通请求与流式读取同一出口。
- **`ResponseType.Stream` 取消同样抛 `ERR_CANCELED`**：直接对 `Response.Data` 流调用 `ReadAsync` / `CopyToAsync` / `Read` 并传入自己的令牌，或请求令牌（方法参数 / `config.CancellationToken`）取消，最终交给调用方的都是带 `ERR_CANCELED` 的 `AxiosException`（原始 OCE 留在 `Cause`）；超时仍为 `ECONNABORTED`。归属判定顺序固定为 `Caller → Canceled → Timeout → None`，只影响归属与错误码，不再有任何一路原样抛出 OCE。**不要用 `catch (OperationCanceledException)` 判断流读取消**，请检查 `AxiosException.Code`。

```csharp
// 调用方承诺「15 秒内一定给出结果」：重试 5 次 × 单次 10s 也不再是上界
await client.GetAsync("orders/scan", c =>
{
    c.Timeout  = TimeSpan.FromSeconds(10);   // 单次尝试
    c.Resilience.TotalTimeout = TimeSpan.FromSeconds(15);   // 整条请求
    c.Resilience.Retry.Policy = new RetryPolicy(5, TimeSpan.FromMilliseconds(200));
});
```

- 总预算用尽时下一次尝试**根本不发出**：抛 `ECONNABORTED`，消息指明是第几次（`... before attempt #7 could be sent`）。这一类「根本没发出」不参与熔断失败计数、不重试、也不换端点（见[熔断](#熔断)的失败口径）。
- 被总预算压小的那次超时，消息会写 `(remaining Resilience.TotalTimeout budget; Timeout = 10000ms)`，避免误判成 `Timeout` 没生效。
- 两种耗尽的错误码相同，要区分就用 `AxiosException.IsTotalTimeoutExceeded`：`true` = 发出前就耗尽（别重试，预算已经空了），`false` = 发出后超时（下游不响应，换端点有意义）。
- **被预算压小的那次超时到点即收手**：上限本来就取自剩余总预算，到点时剩下的零头（不足一个计时器 tick）已经发不出任何有意义的尝试，因此直接报「预算耗尽」而不是拿这点余额再发一次；同理，退避睡不完剩余预算时也不白等，立刻退回令牌并报耗尽。
- 排队时间**计入** `Resilience.TotalTimeout`（`Timeout` 则不计排队）——总预算量的是调用方感知的时长。
- 剩余预算比 `Resilience.Concurrency.QueueTimeout` 更紧时，**排队等待会被剩余预算截断**，失败码是 `ECONNABORTED` 而不是 `ERR_QUEUE_TIMEOUT`（报错要指向真正的约束）；`QueueTimeout` 为空（一直等）时同样受这条约束。
- **流式正文也在预算之内**：正文读取沿用被预算压小后的剩余额度，读到一半到点即 `ECONNABORTED`；长连接（SSE / 大文件下载）不要设 `Timeout` / `Resilience.TotalTimeout`，改用取消令牌退出。
- `Resilience.TotalTimeout = null` 不设总预算（默认），`Zero` 与 `null` 同义，负值 `ERR_BAD_OPTION_VALUE`；按请求显式写 `null` 可以关掉实例默认的总预算。

### 进度、限速与大小上限

三件事都在一次 `configure` 里：进度回调、速率上限、请求 / 响应体大小上限。

```csharp
await http.PostAsync("upload", payload, c =>
{
    c.Observability.OnUploadProgress   = e => Console.WriteLine($"↑ {e.Progress:P0} {e.Rate / 1024:F1} KB/s 剩余 {e.Estimated}");
    c.Observability.OnDownloadProgress = e => Console.WriteLine($"↓ {e.Progress:P0} {e.Rate / 1024:F1} KB/s");
    c.Transport.MaxRate = 1024 * 1024;                 // 单向限速 1 MB/s
    c.Limits.MaxRequestBytes = 50 * 1024 * 1024;      // 请求体上限，发送前拦截
    c.Limits.MaxResponseBytes = 10 * 1024 * 1024;   // 响应体上限，边读边计数
});
```

`AxiosProgressEvent`：`Loaded` / `Total`（未知 `null`）/ `Progress`（0–1，封顶 100%，`Total` 未知为 `null`）/ `Rate`（平均字节每秒）/ `Estimated`（预估剩余）/ `Upload`。

- 回调**同步**执行，耗时逻辑自行转后台；报告节流 50 ms 一次。
- 回调抛异常会中断请求：上传阶段 `ERR_BAD_REQUEST`、下载阶段 `ERR_BAD_RESPONSE`，原异常在 `Cause`。
- `Limits.MaxResponseBytes`：普通响应边读边累计越界即中止；`ResponseType.Stream` 先按声明长度预检，再读取时累计。
- `Limits.MaxResponseBytes` 默认 `null`（不限，与 axios 的 `maxContentLength = -1` 同口径），库不会为此发告警或日志：缓冲型响应（非 `ResponseType.Stream`）会把整个正文读进内存，对不可信或体量未知的下游，生产环境请显式设上限。
- `Content-Length` 只是提示，不是截断依据：声明值比实际正文短时（代理改头、自定义 `HttpContent`、注入的 Handler 不按声明截断）照样读到流结束，既不丢尾部也不算漏 `Limits.MaxResponseBytes`。
- `Limits.MaxRequestBytes` 只对能算出长度的正文生效（JSON / 表单 / `byte[]` / 可 seek 流）；multipart 与长度未知的正文（不可 seek 的流、`IAsyncEnumerable` 异步序列）不拦截。
- 负值一律 `ERR_BAD_OPTION_VALUE`；`MaxRate = 0` 等于不限速。
- `MaxRate` 的计时从**实际传输首字节**起算：拿到响应到开始读之间的空档（线程排队、业务先做别的事）不算已消耗预算，晚读不会让限速变松。

### 发送前钩子（签名 / 最终请求）

需要在**真正发出之前**改写最终请求（签名、时间戳）时，用 `OnSendingRequest`：

```csharp
await client.PostAsync<OrderResult>("orders", order, c =>
{
    c.Observability.OnSendingRequest = async (request, config, ct) =>
    {
        // request.Content 已是按 `Body` 序列化好的最终字节
        var body = request.Content is null
            ? Array.Empty<byte>()
            : await request.Content.ReadAsByteArrayAsync(ct);

        request.Headers.TryAddWithoutValidation(
            "X-Signature", Sign(request.Method.Method, request.RequestUri!.PathAndQuery, body));
    };
});
```

- 触发时点：URL 已合并、正文已序列化、请求头已写入，**取连接与发送之前**；拿到的 `HttpRequestMessage` 就是这条请求真正使用的对象。
- 每次尝试各触发一次：重试 / 换端点 / 对冲的每条分支都重新触发，所以时间戳、nonce 每次发送都是新的。
- 要在多次尝试间**保持不变**的键（幂等键）不要写在这里 —— 每次重试都会换一个值，等于把同一份写操作当成两次。请用 [`Resilience.Idempotency`](#幂等键)。
- 改写只作用于本次发送：库在触发前给本次尝试复制一份请求消息（正文共享），第一次尝试写进头里的内容不会被后续尝试复制叠加。
- 读正文的代价：已缓冲的正文（JSON / 表单 / `byte[]` / `string`）直接取字节；流式请求体（`IAsyncEnumerable<byte[]>`、不可 seek 的 `Stream`）会被读走，包了进度 / 限速的内容会再序列化一遍 —— 签名这类场景请只对已缓冲的正文下手，或只签请求头。
- 抛异常即中断请求（`ERR_BAD_REQUEST`，原异常在 `Cause`），此时不占连接、请求没有发出；钩子里抛出的取消 / 超时按本次尝试归属（`ERR_CANCELED` / `ECONNABORTED`）。
- 钩子换掉正文同样受 `Limits.MaxRequestBytes` 约束：换上的内容长度已知且超限时按 `ERR_BAD_REQUEST` 拒绝（长度未知的正文同上面 大小上限所述，不拦截）。
- 按请求设置会整体覆盖实例默认，与其余 `Observability` 项一致。

### 原始响应字节

需要在**反序列化之前**拿到响应正文（解密、验签、留档审计）时，用 `OnRawResponse`：

```csharp
await client.GetAsync<User>("users/1", c =>
{
    c.Observability.OnRawResponse = (response, bytes) =>
    {
        Console.WriteLine($"{response.Status} {response.MediaType} {bytes.Length}B");
        audit.Write(response.GetHeader("X-Trace"), bytes.Span);
    };
});
```

- 触发时点：正文读完、文本解码与反序列化（以及 `transformResponse`）之前；拿到的是 `HttpClient` 交付的字节（已按 `AutomaticDecompression` / `Decompression` 解压）。
- 参数是 `AxiosResponseInfo`：`Status` / `StatusText` / `Headers`（与 `response.Headers` 同一实例）/ `MediaType` / `CharSet`，可用 `GetHeader` 取单个头。
- 状态码被拒绝时，正文照样先读完再抛错，所以 4xx / 5xx 也会触发（便于记录错误体）。
- `ReadOnlyMemory<byte>` 只在回调期间有效，需要保留请自行复制。
- 只对**非流式**的网络响应触发：`ResponseType.Stream` 没有完整字节，短路响应不经过网络读取路径。
- 每读一份正文触发一次：被重试放弃、被对冲丢弃的分支同样触发，对冲时多条分支会**并发**触发（回调需线程安全）；回调同步执行，抛异常中断请求（`ERR_BAD_RESPONSE`，原异常在 `Cause`）。
- 按请求设置会整体覆盖实例默认，与其余 `Observability` 项一致。

#### 响应对象上的正文

回调之外，响应对象本身也带两个只读入口，返回后照样能读：

| 成员 | 何时有值 | 说明 |
| --- | --- | --- |
| `RawText` | 状态码被 `ValidateStatus` 拒绝，或正文解析 / `transformResponse` 失败时 | 错误体原文（字符串）。正常响应不保留整段文本，避免大正文跟着响应对象一起存活；解析失败时 `Data` 为 `null` 而这里仍是原文；流式失败会先抢救一段；原始字节用 `CaptureRawResponseBody` |
| `RawBody` | 打开 `Observability.CaptureRawResponseBody` 的非流式响应 | `ReadOnlyMemory<byte>?`，与 `OnRawResponse` 看到的字节一致（已解压、未解码）。默认 `null`，不额外占内存 |

```csharp
var response = await client.GetAsync<User>("users/1", c => c.Observability.CaptureRawResponseBody = true);

logger.LogInformation("raw body: {Body}", Encoding.UTF8.GetString(response.RawBody!.Value.Span));
```

- `RawBody` 是只读视图而不是 `byte[]`：底层数组可能与缓存条目共用，交出去改了就污染缓存。空正文是长度 0，不是 `null`。
- 打开后整段正文随响应对象一起存活，只在需要排查的那几次请求上开；长期留档用 `OnRawResponse` 自己复制。
- 流式响应（`ResponseType.Stream`）没有完整字节，开了也没有 `RawBody`；短路 / 缓存命中的正文不受此限。
- 失败时已抛出的异常上也有一份捷径：`AxiosException.ResponseBody` 就是 `Response.RawText`，不必先判空 `Response`。

### 方法与 URL

| 方法 | 说明 |
| --- | --- |
| `GetAsync` / `PostAsync` / `PutAsync` / `PatchAsync` / `DeleteAsync` / `HeadAsync` / `OptionsAsync` | 每个都有「非泛型 → `JsonElement`」与「泛型 → `T`」两种，都可带 `data`、`configure`、`CancellationToken` |
| `PostFormAsync` / `PutFormAsync` / `PatchFormAsync` | 默认按 `multipart/form-data` 发送 |
| `RequestAsync(config)` / `RequestAsync<T>(config)` | 传完整 `AxiosConfig` |
| `GetUri(config)` / `GetUri(c => ...)` | 只拼 URL，不发请求 |
| `Interceptors` / `Defaults` / `Pool` / `UsesConnectionPool` / `AttemptPipeline` | 实例成员 |

以上七个方法还各有一套直接收完整 `AxiosConfig` 的重载（`GetAsync(url, config)` / `PostAsync(url, config)` …）：`config.Url` / `config.Method` 会被 `url` / 方法名覆盖，其余项按常规合并到实例默认值。`PostAsync(url, null)` 因此按「没有要传的配置」处理，等价于 `PostAsync(url)`（发空正文），不再抛 `ArgumentNullException`。

- `url` 可相对（拼 `BaseAddress`）可绝对；`null` / 空白串抛 `ERR_INVALID_URL`——要请求基地址本身请显式传 `"/"`。
- `config.Url` 留空（`null`）同样表示请求 `BaseAddress` 本身，与传 `"/"` 完全等价：尾斜杠属于地址的一部分，会原样保留。
- `Method` 直接用 BCL 的 `HttpMethod`，自定义谓词照发：`new HttpMethod("PROPFIND")`。
- 路径段特殊字符用 `AxiosUrl.EncodePathSegment(value)` 转义。
- `BaseAddress` 按 RFC 3986 §5.2.2 的组件规则解析，不做字符串拼接：
  - base 的 query 只在引用路径为空时继承（引用自己带 query 则以引用为准）；引用带非空相对路径时，base 的 query 被丢弃，不会偷偷拼到新路径后面；
  - base 的 fragment **一律丢弃**；相对引用自带的 fragment 保留在最终 URI 中（fragment 不会发往服务端）；
  - `GetUri` 与实际发送共用同一条解析路径，两者结果一致。

### 在途并发上限

`MaxRate` 限的是**字节速度**，拦不住「每秒几百个小请求」——那来自同时在途的请求条数：

```csharp
var client = Axios.Create(c =>
{
    c.Resilience.Concurrency.MaxInFlight = 4;                 // 本实例最多 4 条在途
    c.Resilience.Concurrency.MaxQueue = 16;                       // 最多 16 条排队，再多就以 ERR_QUEUE_FULL 拒绝
    c.Resilience.Concurrency.QueueTimeout = TimeSpan.FromSeconds(2);    // 排上了最多等 2 秒
});

await client.GetAsync("fast", c => c.Resilience.Concurrency.QueueTimeout = TimeSpan.Zero);   // 满了立刻失败
```

| 项 | 默认 | 语义 |
| --- | --- | --- |
| `MaxInFlight` | `null`（不限） | 本实例同时在途条数；正整数 |
| `MaxQueue` | `null`（不限） | 排队深度上限：超出立刻 `ERR_QUEUE_FULL`；正整数，只在设了 `MaxInFlight` 时起作用 |
| `QueueTimeout` | `null`（一直等） | 等槽位上限：`null` 一直等 / `Zero` 不排队 / `>0` 超时失败；实际等待还被 `Resilience.TotalTimeout` 剩余预算封顶 |

- 排队超时抛 **`ERR_QUEUE_TIMEOUT`**，队列满抛 **`ERR_QUEUE_FULL`**（都不是 `ECONNABORTED`）：请求根本没发出去，该降速 / 扩容 / 退避。
- 两者都是「入口太挤」而不是「下游失败」：默认策略不重试、也不换备用端点——换端点解决不了本实例的槽位竞争，调用方退避后再试。
- 中途某次尝试撞上闸门拒绝时，如果前面已经有真实失败，抛出的仍是那条真实失败（与对冲同一口径）：「没排上队」不会盖掉能定位问题的故障。
- 拒绝计数在 `axiosnet.client.gate.rejections`（`axiosnet.gate.reason` = `queue_timeout` / `queue_full`），可与 `gate.queue_depth` 一起看背压水位。
- `MaxQueue` 与 `MaxInFlight` 一起在首个受限请求时固化：按请求写不同值、或构造后改 `Defaults` 都抛 `ERR_BAD_OPTION_VALUE`（按请求没提这一项不算表态，沿用闸门的值）；要「不排队」用 `QueueTimeout = TimeSpan.Zero`。
- 排队**不计入** `Timeout`（重试每次尝试各自计一次），排队期间可用 `CancellationToken` 取消。
- 槽位覆盖「真正在途」全程：重试每次尝试各自取槽（退避不占位），**流式响应一直占到正文流释放**。
- **自锁风险**：在拦截器 / 进度回调里用同一实例发子请求会互抢槽位，请给 `QueueTimeout` 设上限或换实例。
- **与重试同时开**：`ERR_QUEUE_TIMEOUT` / `ERR_QUEUE_FULL` 本身不重试 —— 闸门满载说明本实例在途已顶格，立刻重发只会再撞同一个闸门。要「等并发降下来再试」，请在调用方外层自己做（降速 / 扩容 / 换实例）。
- 与 `MaxConnectionsPerServer` 分工不同：那个管**连接**（h2 下一条连接可跑很多请求），这里管**请求**。

## 错误处理

所有失败统一抛 `AxiosException`：

```csharp
try
{
    await client.GetAsync("missing");
}
catch (AxiosException ex)
{
    Console.WriteLine(ex.Code);         // ERR_BAD_REQUEST
    Console.WriteLine(ex.Status);       // 404
    Console.WriteLine(ex.Message);      // Request failed with status code 404: {...}（附服务端正文片段）
    Console.WriteLine(ex.ResponseBody); // 服务端错误体原文（完整，不截断）
    Console.WriteLine(ex.Cause);        // 底层异常，可能为 null
    Console.WriteLine(ex.ToJson());     // 结构化快照，可直接进日志
}

if (Axios.IsAxiosError(ex) && !Axios.IsCancel(ex)) { /* ... */ }
```

状态码被拒绝（4xx / 5xx）或正文解析失败时，`Message` 会附上响应正文片段（最多 512 字符，换行折成空格）：只打 `ex.Message` 也能看到服务端给出的原因。完整原文用 `ex.ResponseBody`（等价于 `ex.Response.RawText`）；`ToJson()` 里对应 `responseBody`。

| Code | 触发场景 |
| --- | --- |
| `ERR_BAD_REQUEST` | 4xx；请求体超 `Limits.MaxRequestBytes`；请求体序列化失败；上传进度回调抛异常；声明式请求装配失败（路径 / 查询 / 请求头 / 正文取值或格式化） |
| `ERR_BAD_RESPONSE` | 5xx；响应无法反序列化为目标类型；响应体超 `Limits.MaxResponseBytes`；泛型与 `ResponseType` 不匹配；下载进度回调 / 原始字节回调 / `transformResponse` 抛异常 |
| `ECONNABORTED` | 超过 `Timeout`（含流式正文阶段），或 `Resilience.TotalTimeout` 总预算已耗尽 |
| `ERR_NETWORK` | 连接被拒 / DNS 失败 / 连接中断 / 正文截断 |
| `ERR_CANCELED` | `CancellationToken` 取消，或客户端已释放 |
| `ERR_QUEUE_TIMEOUT` | 并发槽位一直占满，超过 `QueueTimeout`（请求未发出） |
| `ERR_QUEUE_FULL` | 排队深度已达 `MaxQueue`（请求未发出、也没入队） |
| `ERR_CIRCUIT_OPEN` | 熔断打开，请求**没有发出**（`RetryAfter` 给建议等待） |
| `ERR_POOL_EXHAUSTED` | 连接池 origin 基数饱和（全部在途、无可驱逐空闲节点），请求未发出；可观测、默认不自动重发，降扇出 / 调大上限 / `Clear()` 回收后再重试 |
| `ERR_INVALID_URL` | URL 非法、相对地址但没有 `BaseAddress`、非 http(s) scheme、长度超过 65519 字符（与 .NET 10 之前的 `Uri` 上限一致，由库自己守住） |
| `ERR_TOO_MANY_REDIRECTS` | 跟随重定向超过 `Transport.MaxRedirects`（`MaxRedirects = 0` 时用 `ERR_BAD_REQUEST` + 独立文案，表示「本实例禁止跟随」，与「超限」可区分） |
| `ERR_BAD_OPTION_VALUE` | 选项取值非法（按请求写实例级选项、multipart 头体矛盾、`Params` 是标量、重试 / 熔断配置越界等） |

分支判断优先用 `ex.ErrorCode`（枚举，`switch` 能穷举）或 `ex.Code`（与 axios 对齐的字符串），不要解析消息文本：

```csharp
switch (ex.ErrorCode)
{
    case AxiosErrorCode.ConnAborted: /* 超时：同类请求可以再试一次 */ break;
    case AxiosErrorCode.BadRequest:  /* 4xx：请求本身有问题，重发没用 */ break;
    case AxiosErrorCode.Canceled:    return;
    default: throw;
}
```

`ErrorCode` 与 `Code` 一一对应；`Code` 不是内置常量（例如派生类写入的自定义码）时取 `AxiosErrorCode.Unknown`，此时仍以 `Code` 为准。

自定义合法状态码（默认 2xx）：

```csharp
await client.GetAsync("maybe-404", c => c.ValidateStatus = s => s is >= 200 and < 500);
```

## 配置

`AxiosConfig` 顶层是请求本身（13 个可配置叶子），其余按领域收进六个分组：`Limits` / `Serialization` / `Resilience` / `Transport` / `Observability` / `Caching`。连同顶层共 7 组、71 个叶子（13 + 3 + 9 + 19 + 10 + 7 + 10）；每个叶子只有一份存储、一份注释、一个入口，实例默认值与按请求覆盖写同一组属性。

### 配置速查

`AxiosConfig` 顶层（请求本身）：

| 成员 | 默认 | 说明 |
| --- | --- | --- |
| `BaseAddress` | `null` | 等价 axios `baseURL` |
| `Url` | `null` | 相对或绝对地址 |
| `Method` | `Get` | BCL `HttpMethod`，支持自定义谓词 |
| `Headers` | 空 | `AxiosHeaders`，大小写不敏感，写入即校验 |
| `Params` | `null` | 对象 / 字典 / 集合 / 已编码串 |
| `ContentType` | `application/json` | 决定 `Body` 的编码方式 |
| `Body` | `null` | 请求体 |
| `Compression` | `null`（不压缩） | 请求体压缩：`Gzip` / `Deflate` / `Brotli`，`None` 覆盖实例默认 |
| `Timeout` | `Zero`（不超时） | 单次尝试的预算（建连 + 响应头 + 正文） |
| `ResponseType` | `Json` | `Json` / `Text` / `ArrayBuffer` / `Stream` / `Dynamic` |
| `CancellationToken` | `default` | 外部取消 |
| `Response` | `null` | 响应阶段可读（状态码 / 头 / 媒体类型） |
| `ShortCircuit` | `null` | 短路响应：不发请求直接交付（只对本次请求生效） |
| `ValidateStatus` | `null`（2xx） | 返回 `false` 抛 `AxiosException` |
| `Caching` | `null` | 响应缓存（`AxiosCache`）：命中不发请求，跨请求复用（配置项见 `AxiosCacheOptions`） |

> 表中 `Response` 只在响应阶段可读、`Caching` 是分组入口（`AxiosCacheOptions` 下另有 10 个叶子），二者都不计入顶层的 13 个可配置叶子。

`Limits` 子对象（体积与分帧上限，三项的唯一入口；首次访问才创建）：

| 成员 | 默认 | 说明 |
| --- | --- | --- |
| `Limits.MaxResponseBytes` | `null`（不限） | 响应体上限，边读边计数（原 `Limits.MaxContentLength`） |
| `Limits.MaxRequestBytes` | `null`（不限） | 请求体上限，发送前拦截（原 `Limits.MaxBodyLength`） |
| `Limits.MaxStreamFrameBytes` | `null`（= 8 MB） | 流式单帧上限，合法 1–64 MB（原 `Limits.MaxStreamFrameLength`） |

其余四个子对象（首次访问才创建）：

| 子对象 | 关键成员（默认值 / 说明） |
| --- | --- |
| `Serialization` | `JsonOptions`（Web 风格）/ `BodySerializers`（实例注册表，客户端上必有）/ `TextEncoding`（`null` = 发送 UTF-8、接收自动识别）/ `QueryEncoder` / `FormFieldName` / `CollectionFormat`（`Brackets`）/ `ValueText` / `TransformRequest` / `TransformResponse` |
| `Resilience` | `Retry`（`Policy` = `null` 不重试 / `OnRetry`）/ `Circuit`（`Policy` = `null` 不熔断 / `OnRejected`）/ `Hedging`（`MaxHedges` = `0`，上限 16 / `Delay` = 80ms）/ `Failover`（`Endpoints` = `null` / `FailureThreshold` = 2 / `Cooldown` = 30 秒 / `MaxCooldown` = 5 分钟）/ `Concurrency`（`MaxInFlight` / `MaxQueue` / `QueueTimeout`，均 `null`）/ `Idempotency`（`Enabled` = `false` / `HeaderName` = `Idempotency-Key` / `Key` / `KeyFactory`）/ `TotalTimeout`（`null` = 不设，跨重试 / 端点 / 对冲共享）/ `AllowUnsafeRetry`（`false`，非幂等放行的唯一开关） |
| `Transport` | `Proxy` / `Credentials`（`null`，仅实例级）/ `Pool`（`null`，建池时固化）/ `HttpVersion`（`null` = 2.0）/ `VersionPolicy`（`null` = `OrLower`）/ `Profile`（`null` = 默认档）/ `Auth`（`null`；静态 Basic / 令牌 / 完整头值 / 提供方四选一）/ `WithCredentials`（`false`，仅实例级）/ `MaxRedirects`（5，仅实例级）/ `MaxRate`（`null` = 不限速） |
| `Observability` | `Capture`（`null` = `TelemetryCapture.Path`；`Off` / `Path` / `Query` / `NoUrl`）/ `OnUploadProgress` / `OnDownloadProgress` / `OnRawResponse` / `CaptureRawResponseBody`（`false`）/ `OnSendingRequest` / `TimeoutMessage` |

> 按请求显式赋 `null` 可以关掉实例默认的 `Resilience.Retry.Policy` / `Resilience.Circuit.Policy` / `Resilience.Failover.Endpoints`，这与「没设置」是两种语义。

### 客户端

三种客户端形态，底层能力完全一致：

| 形态 | 创建方式 | 适用 |
| --- | --- | --- |
| 静态门面 | `Axios.GetAsync(...)` / `Axios.Defaults` / `Axios.Interceptors` | 脚本、单站点应用 |
| 实例 | `Axios.Create(c => ...)` 或 `new AxiosClient(config)` | 常规用法：长生命周期持有，一套默认值 + 一条连接池 |
| 声明式 | `client.Create<T>()` 或 `new ApiClient(baseAddress)` | 接口多，用模型或接口描述请求（见[模型式 API](#模型式-api)） |

`Axios.Create("https://api.example.com")` 是 `Axios.Create(c => c.BaseAddress = ...)` 的简写，可再传第二个参数 `Action<AxiosConfig>` 配置默认项。

**实例默认值 vs 单次请求**：`Axios.Create` 里配的是实例默认值，快捷方法末尾的 `configure` 配本次请求。`configure` 拿到的是实例默认值的**副本**，所以能真正摘掉默认头：

```csharp
await http.GetAsync("slow", c => c.Timeout = TimeSpan.FromSeconds(30));   // 只影响这次
await http.GetAsync("x", c => c.Headers.Delete("X-Foo"));                 // 默认头被摘掉
```

- **只在实例级生效**：`Transport.Proxy` / `Credentials` / `WithCredentials` / `MaxRedirects` / `Pool`（建池时固化）、`Resilience.Concurrency.MaxInFlight` / `Resilience.Concurrency.MaxQueue`（首个受限请求时固化）。单次请求写**不同的值**会抛 `ERR_BAD_OPTION_VALUE`（复述实例的值则放行），避免「设了没生效」。
- **`Params` / `Body` 按「有没有赋值过」合并，不按是否为 `null`**：不碰这两项 = 继承实例默认值；显式写 `c.Params = null` / `c.Body = null` = 摘掉实例默认值（与 `Headers.Delete` 用「删除」表达摘头同一个道理）。所以 `PostAsync(url)` 这种不传 `data` 的调用仍会带上 `Defaults.Body`，要发空正文得显式写 `Body = null`。
- **`Observability` 的七个叶子同样按「有没有赋值过」合并**：省略 = 继承实例默认值；按请求显式写 `null`（布尔项写 `false`）= 清除 / 关掉实例默认值。适用于 `Observability.Capture` / `OnUploadProgress` / `OnDownloadProgress` / `OnRawResponse` / `CaptureRawResponseBody` / `OnSendingRequest` / `TimeoutMessage`，与 `Body` / `ContentType` / `Compression` / `CancellationToken` 同一口径。
- **一个站点一个实例，不要按请求新建**：每个实例自建一条连接池，`Axios.Create` / `new AxiosClient` 各建一套 Handler；按请求新建等于退化成 `new HttpClient()` 那个经典坑——长连接不复用，连接与句柄持续增长。要换默认值就换实例，要共享连接就共享池（见[连接池](#连接池)）；DI 场景直接用 `AddAxiosClient` 注册单例。
- 实例用 `using`（或交给 DI 容器持有）收尾，连接池与 Handler 随之释放；在途请求不会被打断，只是不再接受新请求。
- **静态门面可以重置**：`Axios.Reset()` 释放全局默认实例，下次访问 `Axios.Defaults` / `Axios.Interceptors` 或发请求时按全新配置重建。在途请求不受影响（释放不打断它们），旧的默认值与拦截器一并丢弃——典型场景是测试之间隔离全局状态，或默认实例被误释放后恢复（释放过的实例对所有请求恒抛 `ERR_CANCELED`）。

#### 回放 `response.Config`

`AxiosResponse.Config`（以及 `AxiosException.Config`）是**那一次请求的生效配置**：实例默认值、末尾 `configure`、请求拦截器都已经合进去。把它交回 `RequestAsync` / 收 `AxiosConfig` 的重载，会当作「按请求项」重新合并到**当前实例**的默认值上：

```csharp
var first = await a.GetAsync("items/1");            // a 的默认值 + 本次请求项
var again = await b.RequestAsync(first.Config!);    // b 的实例级设置 + first 的按请求项
```

**属于实例的项不跟着回放**——永远取当前实例的值，在那个对象上重写也无效：

| 项 | 回放时的取值 |
| --- | --- |
| `BaseAddress` | 当前实例的默认值 |
| `CancellationToken` | 当前实例的默认值（要换令牌，用快捷方法的 `cancellationToken:` 参数） |
| `Transport.Auth` / `Proxy` / `Credentials` / `WithCredentials` / `Pool` / `MaxRedirects` | 当前实例的默认值（上一次的按请求 `Auth` 不会被带走） |
| `Resilience.Concurrency.MaxInFlight` / `Resilience.Concurrency.MaxQueue` | 当前实例的默认值 |
| `Serialization.BodySerializers` | 当前实例的注册表（来源实例登记的媒体类型不会带过来） |
| `Serialization.ValueText` | 当前实例的默认值（来源实例的钩子是它的实例级设置，不跟着配置搬过来；按请求显式给钩子请走直接调用的 `configure`） |
| `Observability` 的七个叶子（`Capture` / `OnUploadProgress` / `OnDownloadProgress` / `OnRawResponse` / `CaptureRawResponseBody` / `OnSendingRequest` / `TimeoutMessage`） | 来源实例继承来的回调不跟着回放（快捷方法产出的 `Config` 也一样）；调用方显式写进配置的回调 / `null` 保留，因为那是请求级意图 |
| `ShortCircuit` | 不生效（短路响应只属于那一次调用） |

其余按请求项照搬，分三组：

- 请求本身：`Url` / `Method` / `Headers` / `Params` / `Body` / `ContentType` / `Compression` / `ResponseType`；
- 时间与策略：`Timeout` / `Resilience.TotalTimeout` / 重试 / 熔断 / 对冲 / 备用端点 / 遥测（仅调用方显式赋值项，回调口径见上表）；
- 其它：序列化其余项 / 大小上限其余项 / `Transport.HttpVersion` / `VersionPolicy` / transform 链（作为追加段，与实例默认段按引用去重）。

**两条边界**

- 「实例来源」只针对**库产出的配置**：`response.Config`、它的 `Clone()` 副本、快捷方法内部组装的生效配置。自己 `new AxiosConfig { BaseAddress = ... }` 提交的配置不在此列，写什么就用什么。
- 要换主机 / 换令牌再发一次：换实例（`Axios.Create(c => c.BaseAddress = ...)`）或自己写 `new AxiosConfig { Url = ..., Method = ... }`；只想在当前实例上补改几项，用 `RequestAsync(c => ...)` 这类 Action 重载（它在当前实例默认值的副本上修改）。

### 拦截器

```csharp
// 请求拦截器：按注册顺序【逆序】执行，同步 / 异步两种重载
client.Interceptors.Request.Use(config =>
{
    config.Headers.Set("X-Trace-Id", TraceId.New());
    return config;
});

client.Interceptors.Request.Use(async config =>
{
    config.Headers.Set("Authorization", $"Bearer {await tokenService.GetTokenAsync()}");
    return config;
});

// 响应拦截器：按注册顺序执行；rejected 分支可吞错并返回兜底响应
client.Interceptors.Response.Use(
    onFulfilled: response => response,
    onRejected: ex =>
    {
        var error = (AxiosException)ex;
        if (error.Code == AxiosException.ErrBadRequest && error.Status == 401)
        {
            // 刷新令牌后重放，或返回兜底数据
        }
        throw ex;
    });

// 移除
var id = client.Interceptors.Request.Use(cfg => cfg);
client.Interceptors.Request.Eject(id);
```

> **拦截器写 `Authorization` 头，只适合「授权完全由拦截器负责」的客户端**。同时配了 `CredentialProvider` 或 `Transport.Auth` 时，显式 `Authorization` 头与凭据来源视为配置冲突，发出前抛 `ERR_BAD_OPTION_VALUE`——要按请求换身份请改凭据槽位（`config.Transport.Auth = AxiosCredential.Bearer(token)`），改完库会按新槽位重新解析一次凭据。另外 401 重放会重跑请求拦截器，时间戳 / trace id 这类值在重放时会变；需要跨尝试稳定的键用 [`Resilience.Idempotency`](#幂等键)。

**返回全新配置时的合并规则**：请求拦截器可以返回自己 `new` 的 `AxiosConfig`（或调用方原始配置）。管线会把它再合并一次到实例默认值上，`BaseAddress`、超时、连接级选项等默认值因此照常生效。合并只取「显式设置」的项，拦截器刚写入的值不会被实例默认值覆盖。返回已合并配置（`response.Config`）或它的 `Clone()` 时不再二次合并；在原 `config` 上修改并返回它同样不会触发二次合并。

### 数据转换与序列化

两条 transform 链负责「同一种格式内的加工」，序列化器负责「正文整体是什么格式」。请求方向先 transformRequest 再编码，响应方向先反序列化再 transformResponse。

#### 数据转换

与 axios 同构的两条链：请求方向在拦截器之后、编码之前；响应方向在反序列化之后。

```csharp
var http = Axios.Create(c =>
{
    c.TransformRequest.Add((data, cfg) =>
    {
        if (data is IDictionary<string, object?> map)
        {
            map["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        return data;
    });

    // 统一剥掉 { code, data } 外壳
    c.TransformResponse.Add((data, cfg) =>
        data is JsonElement { ValueKind: JsonValueKind.Object } e && e.TryGetProperty("data", out var inner)
            ? inner
            : data);
});

await http.GetAsync("users/1", c => c.TransformResponse.Add((d, _) => Normalize(d)));   // 实例链在前，本次在后
```

- 链是 `IList<Func<object?, AxiosConfig, object?>>`，返回 `null` 合法，会继续往后传。
- `TransformRequest` 的返回值仍按 `ContentType` 编码。
- `TransformResponse` 在 `Json` / `Text` / `ArrayBuffer` / `Dynamic` 下执行，`Stream` 不执行（数据还没读）。

#### 序列化

请求体 / 响应体默认由 System.Text.Json 处理，可整体替换。

**JSON 设置三层覆盖**（后者优先）：全局 `AxiosJson.Options` → 实例 `c.Serialization.JsonOptions` → 单次请求同名项。默认 Web 风格（输出 camelCase、输入大小写不敏感）。替换 `AxiosJson.Options` 请给新实例（STJ 会冻结用过的选项）。

**内置 XML**：

```csharp
using AxiosNet.Serialization;

// 方式 A：进程兜底（按媒体类型都匹配不到时才用），请求头自动变成 application/xml
AxiosContent.Default = XmlContentSerializer.Instance;
await http.PostAsync("orders", order);

// 方式 B：按 Content-Type 自动选（同一应用混用多种格式）
AxiosContent.Register(AxiosContentType.Xml, XmlContentSerializer.Instance);
await http.PostAsync("orders", order, c => c.ContentType = AxiosContentType.Xml);

// 方式 C：只给这个实例登记（推荐：多个站点 / 测试之间互不干扰）
http.Defaults.Serialization.BodySerializers.Register(AxiosContentType.Xml, XmlContentSerializer.Instance);
```

**优先级**（从左到右降低）：实例 / 请求 `Serialization.BodySerializers`（精确登记 → 表内默认）→ 进程 `AxiosContent.Register` 按媒体类型登记 → 进程 `AxiosContent.Default` 兜底 → 内置 JSON。`Default` 只兜底，不会绕过按媒体类型登记的项。

- 实例注册表是每客户端一份的 `ContentSerializerRegistry`（查表规则与全局表一致：精确匹配 → `text/xml` 等别名 → 结构化后缀 `+xml`），登记不影响其它实例和全局表；测试里改全局表会污染同进程的其它用例，需要隔离就用实例表。
- 按请求替换整张表：`c => c.Serialization.BodySerializers = new ContentSerializerRegistry()`，本次请求只认这张新表，不回流到实例。
- 表对象是共享的：按请求拿到的 `c.Serialization.BodySerializers` 就是实例那张表，直接 `.Register(...)` 会改到实例，要只在本次请求生效请 new 一张表再赋值；同一个 `AxiosConfig` 交给多个客户端、或 `Defaults.Clone()` 时也共享同一张表。
- 注册表属实例级：回放 `response.Config` 时注册表取目标实例的，来源实例的登记不会跟着走。

**自定义格式**：实现 `IContentSerializer`（文本格式两个方法；二进制格式再加字节通道 `SerializeToBytes` / `Deserialize(ReadOnlyMemory<byte>)`；可选 `MediaType` 自报媒体类型）。

```csharp
public sealed class YamlSerializer : IContentSerializer
{
    public string? MediaType => "application/yaml";

    public string Serialize(object? value, Type type, AxiosConfig config) => YamlConvert.Serialize(value, type);

    public object? Deserialize(string text, Type type, AxiosConfig config) => YamlConvert.Deserialize(text, type);
}

AxiosContent.Default = new YamlSerializer();
```

- 序列化器可写请求头（`config.Headers.Set(...)`），拿到的 `config` 是本次请求独占副本，不会污染实例默认值。
- 响应正文里的业务对象统一走字节入口（合法 UTF-8 文本直接按字节解析，不做 UTF-16 中转）；正文不是合法 UTF-8 时改走文本入口，坏字节按 U+FFFD 替换，与只解码的老行为一致。文本格式无需实现字节入口，接口默认实现会按响应字符集解码后转调文本入口。
- 表单 / multipart、基础类型、`string` / `byte[]` / `Stream` 不经过序列化器。
- 实现需线程安全（会被并发共享）；异常统一包装成 `ERR_BAD_REQUEST` / `ERR_BAD_RESPONSE`。
- 文本编码：`Serialization.TextEncoding`（`null` = 发送 UTF-8、接收自动识别；设置后两个方向都用它）。
- GBK 等代码页需先 `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)`。**发送侧**遇到解析不出来的 `charset` 直接报 `ERR_BAD_OPTION_VALUE`（不会把 UTF-8 字节标成 gbk 发出去）；**接收侧**把未知 `charset` 当没声明，回落到 BOM / UTF-8。

### 连接与传输

传输相关的按请求项在 `Transport` 子对象；连接池、Handler 与池级别选项在构造客户端时给出。

#### 连接池

`AxiosClient` 默认自建一条按 `scheme://host:port`（以及传输档，见[传输档](#传输档)）隔离的连接池（`HttpConnectionPool`）：每个 origin 一个调优过的 `SocketsHttpHandler`，请求共享 TCP 长连接。

> **不要按请求建客户端**（原因见[客户端](#客户端)）：一个站点一个实例；多个客户端要共享连接，就显式传同一条池（下面的用法 2），DI 里用 `AddAxiosClient` 注册单例。
>
> **丢弃实例前必须 `Dispose`**：未释放的池由后台驱逐定时器持有，GC 收不走，它的连接要等到 `IdleHandlerLifetime`（默认 5 分钟）之后才被回收。

```csharp
using System.Net;
using AxiosNet.Pooling;

// 1) 自建池（构造时配置）
var client = new AxiosClient(poolOptions: new ConnectionPoolOptions
{
    MaxConnectionsPerServer = 32,
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    AutomaticDecompression = true,                                             // 总开关
    Decompression = DecompressionMethods.GZip | DecompressionMethods.Brotli    // 只解这两种（默认 All）
});

// 2) 多客户端共享一条池
await using var sharedPool = new HttpConnectionPool(new ConnectionPoolOptions { MaxConnectionsPerServer = 64 });
using var orderClient = new AxiosClient(pool: sharedPool);
using var payClient   = new AxiosClient(pool: sharedPool);
using var payClient2  = Axios.Create(c => c.BaseAddress = "https://pay.example.com/", sharedPool);

// 3) 看状态 / 手动驱逐
ConnectionPoolStatistics stats = sharedPool.GetStatistics();
sharedPool.EvictIdleEndpoints();

// 4) 命名共享池：按名字取同一条进程级池（options 只在首次创建时生效）
var named = HttpConnectionPool.GetOrCreate("orders", new ConnectionPoolOptions { MaxConnectionsPerServer = 64 });
using var a = Axios.Create(c => c.BaseAddress = "https://order.example.com/", pool: HttpConnectionPool.GetOrCreate("orders"));
using var b = Axios.Create(c => c.BaseAddress = "https://pay.example.com/",   pool: HttpConnectionPool.GetOrCreate("orders"));

HttpConnectionPool.Release("orders");   // 摘除并优雅释放；此后同名再取会新建一条
```

| 选项 | 默认 | 说明 |
| --- | --- | --- |
| `MaxConnectionsPerServer` | 100 | 单站点最大 TCP 连接数，超出的请求在连接层排队 |
| `PooledConnectionLifetime` | 10 分钟 | 连接最长复用时长，到期后下次取用换新（不打断在途）；实际落到 Handler 的值有 ±10% 抖动，避免同一时刻批量断连重连 |
| `PooledConnectionIdleTimeout` | 2 分钟 | 空闲连接关闭 |
| `ConnectTimeout` | 15 秒 | 建连超时 |
| `ConnectCallback` | `null` | 自定义建连：接管「端点 → 流」（UDS / 本机进程内传输 / 自管 DNS）；TLS 仍由 Handler 在返回的流上完成 |
| `MaxResponseHeadersLength` | 64 KB | 响应头体积上限，必须 `> 0`（底层没有「0 = 不限制」的写法）；h1 / h2 都生效，超限按 `ERR_NETWORK` 失败 |
| `IdleHandlerLifetime` | 5 分钟 | origin 节点空闲多久被回收 |
| `EvictionInterval` | 30 秒 | 后台清扫间隔；`<= 0` 不启动定时器 |
| `AllowAutoRedirect` / `MaxAutomaticRedirections` | `true` / 5 | 自动重定向；`MaxRedirects = 0` 关闭 |
| `AutomaticDecompression` | `true` | 自动解压总开关；关掉等价于 `DecompressionMethods.None` |
| `Decompression` | `All` | 解压算法：`GZip` / `Deflate` / `Brotli` 任意组合或 `All`（全开，默认）；`None` 一个都不解，其余位建池时抛 `ERR_BAD_OPTION_VALUE` |
| `EnableMultipleHttp2Connections` | `true` | h2 下允许按 `MaxConnectionsPerServer` 继续加连接 |
| `KeepAlivePingDelay` / `KeepAlivePingTimeout` | `InfiniteTimeSpan`（不发）/ 20 秒 | h2 keep-alive ping 的间隔与等 ACK 的上限；默认与 `SocketsHttpHandler` 一致（关闭），设正值即打开。h1 无影响 |
| `KeepAlivePingPolicy` | `WithActiveRequests` | 何时 ping：`WithActiveRequests`（有在途请求时）/ `Always`（空闲也维持）|
| `MaxEndpointCount` | 1024 | origin 节点上限，满了先驱逐最久未用的空闲节点；若全部在途（无可驱逐），新建节点抛 `ERR_POOL_EXHAUSTED`（请求未发出，可观测、默认不自动重发，降扇出 / 调大上限 / `Clear()` 回收后再重试） |
| `UseCookies` | `false` | 默认档打开后有一份 `CookieContainer`，可通过 `pool.Cookies` 读写（未开启时为 `null`），登出用 `pool.ClearCookies()`；命名传输档各自一份，见[传输档](#传输档) |
| `CookieCapacity` / `CookiePerDomainCapacity` / `CookieMaxCookieSize` | `300` / `20` / `4096` | Cookie 容器容量（总条数 / 单域名条数 / 单条字节数），超出按容器规则淘汰最早的、超长 cookie 直接丢弃；仅 `UseCookies` 打开时生效 |
| `Proxy` / `Credentials` | `null` | 不设代理时**不使用**系统 / 环境代理；要走系统代理请设 `HttpClient.DefaultProxy` |
| `ServerCertificateCustomValidationCallback` / `EnabledSslProtocols` / `ConfigureSslOptions` | `null` | 证书校验、TLS 版本、客户端证书 |
| `HandlerFactory` | `null` | 每个 origin 创建 Handler 时调用，见[自定义 Handler](#自定义-handler) |

- `Dispose()` 是优雅关闭：空闲节点立即释放，有在途请求的节点等租约归还后回收；`Clear()` 立即释放全部节点（会打断在途）。
- DNS 变更的收敛窗口由 `PooledConnectionLifetime` 决定；需要自定义建连（拨到 UDS / 本机传输 / 自管 DNS）时直接设 `ConnectionPoolOptions.ConnectCallback`。
- `ConnectCallback` 与 `MaxResponseHeadersLength` 只作用于库默认 Handler：`HandlerFactory` 里用 `ctx.CreateDefaultHandler()` 建内层 Handler 会带过去，完全自建 Handler 则需自行映射。
- **h2 keep-alive 默认关闭**（与 `SocketsHttpHandler` 一致，不悄悄改变线上行为）：把 `KeepAlivePingDelay` 设成正值（常见 30 秒）即打开，只在有在途请求时发。
- 它对付的是「静默掐断」：AWS ALB 默认 60 秒空闲断连、NAT 表 30–120 秒，客户端以为连接还在池里，下一个请求撞上已关闭的连接 —— 要么多付一次 TLS 握手，要么在 h2 上收到 `GOAWAY` / 流重置（表现为偶发 `ERR_NETWORK`）。代价是一个 17 字节的帧，比一次重连便宜得多。
- 低频高价值调用（webhook 回调、定时任务）可以配 `Always`，连空闲期也维持。反过来，链路上若有**不回应 `PING`** 的代理 / 中间盒，打开后连接会被判死重连（`ERR_NETWORK` 变多）—— 这也是它默认关闭的原因。
- **`UseCookies` 只按域 / 路径隔离，不按业务身份隔离**：默认档一份容器，命名传输档各一份（见[传输档](#传输档)）。多租户、爬虫、按用户切身份的进程请给每个身份一条档，否则不同身份会串 cookie。
- 容器可用 `pool.Cookies` / `pool.GetCookies("档名")` 直接读写（线程安全），用于启动预置或排查某个 cookie 为什么没发出去。
- 登出用 `pool.ClearCookies()`：会换掉**所有档**的容器并回收现有 origin 节点，后续请求因此重建连接；在途请求不打断，可能仍带旧 cookie。
- 开启容器后，请求上的 `Cookie` 头与容器内容是**合并**发送、不能覆盖同名 cookie；`Clear()` / `Dispose()` 都不会清空容器。命名共享池上调用 `ClearCookies()` 会影响所有共用该池的客户端。
- 容器容量默认 300 条 / 单域 20 条 / 单条 4096 字节，超出会静默淘汰最早的；可用 `CookieCapacity` / `CookiePerDomainCapacity` / `CookieMaxCookieSize` 调整。
- **Trailers 目前不读取**：chunked 响应的尾部头（校验和、配额）停留在 `HttpResponseMessage.TrailingHeaders`，没有暴露到 `AxiosResponse`。需要时用 `HandlerFactory` 包一层在正文读完后取，或直接注入自定义 Handler。
- **代理 / 凭据默认是池级单份**：`Proxy` 落在 Handler 实例上，按请求改代理会抛 `ERR_BAD_OPTION_VALUE`。要多出口 IP / 多租户会话，用下面的[传输档](#传输档)在同一条池里挂多套出口，而不是每个出口各建一条池。
- **命名共享池**（用法 4）解决「多客户端想复用连接又不方便传池对象」：key 由调用方给，**不做配置指纹** —— `HandlerFactory` / `ConnectCallback` / `Proxy` / `Credentials` / 证书回调这类引用成员没有可靠的结构等价，两份「长得一样」的配置宁可报错也不共用一条池。
- 命名池的规则只有三条：
  - 同名取同一条；
  - `options` 只在首次创建时生效，重复传一份**不同**的配置抛 `ERR_BAD_OPTION_VALUE`，不静默忽略；
  - 表里的池不归任何客户端所有，客户端 `Dispose` 不会关掉它，收尾用 `Release(key)`。
- 想知道「池是不是被建太多了」看指标 `axiosnet.client.pool.active_pools`（存活池数）与 `.shared_pools`（命名池条目数）。

#### 传输档

同一个池里挂多套出口（代理 / 凭据 / 客户端证书 / Cookie 会话），由请求自己选一套：

```csharp
using AxiosNet.Pooling;

// 默认档：ConnectionPoolOptions 上的 Proxy / Credentials / TLS 字段
var pool = new HttpConnectionPool(new ConnectionPoolOptions
{
    MaxConnectionsPerServer = 64
})
.AddTransport("egress-a", new ConnectionTransportOptions
{
    Proxy = proxyA,
    UseCookies = true                                  // 该档自己的 Cookie 会话
})
.AddTransport("egress-b", new ConnectionTransportOptions
{
    Proxy = proxyB,
    ConfigureSslOptions = ssl => ssl.ClientCertificates = tenantBCerts   // 该档自己的 mTLS 证书
});

using var client = new AxiosClient(pool: pool);

await client.GetAsync("orders", c => c.Transport.Profile = "egress-a");
await client.GetAsync("orders", c => c.Transport.Profile = "egress-b");   // 同一个 origin，另一条出口

// 也可以固定在实例默认值上，单次请求写 null 回到默认档
using var tenantClient = new AxiosClient(
    new AxiosConfig { BaseAddress = "https://api.example.com/", Transport = { Profile = "egress-a" } },
    pool: pool);
```

- **档不继承默认档的传输字段**：`Proxy` / `Credentials` / 证书回调 / `ConnectCallback` / `HandlerFactory` / `UseCookies` 都是一整套。想在某个档上继续用默认档的代理，把同一个 `IWebProxy` 实例赋给它（按引用共享，零成本）。
- **选中命名档后默认档代理不生效，且默认档不能再设代理**：实际 Handler 只取该档的 `Proxy` / `Credentials`；若默认档还设了这两项，按请求发出前直接抛 `ERR_BAD_OPTION_VALUE`（与其它两层出口入口同一口径），不静默忽略。
- **池级策略不按档拆**：连接数上限、连接生命周期、空闲驱逐、压缩、重定向、HTTP/2 与 ping 仍由 `ConnectionPoolOptions` 统一决定 —— 它们描述的是「怎么用连接」，不是「从哪出去」。但上限是**按 Handler 生效**的：每个档一套 Handler，所以 N 个档的实际连接总量上限是 `N × MaxConnectionsPerServer`（想要「整池共用一份预算」得自己按档分摊）。
- **每档每 origin 一个独立节点**：独立的 Handler、独立的 TCP 连接、独立的 Cookie 容器（`pool.GetCookies("档名")` 取）。所以 `MaxEndpointCount` 的计数维度是 **origin × 档**，`GetStatistics().Endpoints[i].Transport` 能看出每个节点属于哪一档。
- **未登记的档直接报错**：`c.Transport.Profile = "missing"` 在发出前抛 `ERR_BAD_OPTION_VALUE`（消息里列出已登记的档）—— 静默回落默认档会把「以为走了专线」变成安全事故。
- **注入自定义 Handler 时档无效**：那条路径不走连接池，写非空档名同样报 `ERR_BAD_OPTION_VALUE`；需要多出口请用池 + `AddTransport`，或在 Handler 里自己选路。
- **档自己的 `HandlerFactory` 优先于池级**：工厂通过 `ctx.Profile` / `ctx.Transport` 拿到本档，可以按档返回不同传输实现；`ctx.CreateDefaultHandler()` 会自动带上本档的出口设置。
- **命名共享池会比较档表**：同一 key 上第二次 `GetOrCreate` 传了不同的档名集合或不同的引用成员，抛 `ERR_BAD_OPTION_VALUE`（与其它选项同一口径）。
- 指标不按档打标签（`pool.*` 保持低基数）：要区分出口用 span 的 `server.address` / 审计事件的端点序号，或 `GetStatistics()`。

#### HTTP 版本

默认 `HttpVersion = Version20` + `VersionPolicy = RequestVersionOrLower`：https 上协商出 h2 就用 h2，服务端只给 1.1 就在同一次 TLS 握手里降级；明文 `http://` 一律 1.1。

```csharp
var client = new AxiosClient();                                       // 默认即可

await client.GetAsync(url, c => c.Transport.HttpVersion = HttpVersion.Version11);   // 退回 1.1

await client.GetAsync(url, c =>                                        // 要求必须 h2
{
    c.Transport.HttpVersion = HttpVersion.Version20;
    c.Transport.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
});

await client.GetAsync(url, c => c.Transport.HttpVersion = HttpVersion.Version30);   // h3，不支持则降级
```

| 配置 | 实际发出 |
| --- | --- |
| 默认（`Version20` + `OrLower`） | https 协商 h2，否则 1.1；明文一律 1.1 |
| `Version20` + `Exact` | 只发 h2，协商不上报错（明文上等于 h2c 先验知识） |
| `Version11`（策略默认） | HTTP/1.1 |
| `Version30`（策略默认） | 有 QUIC 且服务端支持就用 h3，否则降级 |
| `Version30` + `Exact` | 只发 h3；环境不支持（明文 / 无 QUIC）发请求前报 `ERR_BAD_OPTION_VALUE`，服务端不支持报 `ERR_NETWORK` |

- 实际版本看 `response.ProtocolVersion`，请求意图看 `Config.Transport.HttpVersion`。
- 本机能否跑 h3 查 `Http3Support.IsSupported` / `UnsupportedReason`（.NET 6 默认关闭 h3，需 runtimeconfig 的 `System.Net.SocketsHttpHandler.Http3Support=true` 或环境变量 `DOTNET_SYSTEM_NET_HTTP_SOCKETSHTTPHANDLER_HTTP3SUPPORT=1`，且系统有 MsQuic）。
- `HttpVersion` / `VersionPolicy` 是按请求项，实例默认与单次请求都能设，重试沿用。
- `WithCredentials = true` 走 `CredentialCache.DefaultCredentials`，NTLM / Negotiate 这类连接级认证在 h2 上不可用——面向 IIS / 域内服务请显式 `Version11`。

#### 自定义 Handler

两个接入点，区别在于是否走连接池：

| 需求 | 用法 | 走连接池 |
| --- | --- | --- |
| 套一层日志 / 签名 / 埋点 `DelegatingHandler` | `ConnectionPoolOptions.HandlerFactory` | 是 |
| 完全接管连接层（测试桩、自定义传输） | `new AxiosClient(handler: ...)` | 否 |

```csharp
using AxiosNet.Pooling;

// 方式一：每个 origin 创建一次，内层用池配好的 SocketsHttpHandler
using var client = new AxiosClient(poolOptions: new ConnectionPoolOptions
{
    HandlerFactory = ctx => new TraceHandler { InnerHandler = ctx.CreateDefaultHandler() }
});

// 方式二：直接注入，不建池
using var client2 = new AxiosClient(
    defaults: new AxiosConfig { BaseAddress = "https://api.example.com/" },
    handler: new TraceHandler { InnerHandler = new SocketsHttpHandler() },
    disposeHandler: true);
```

- `HandlerFactory` 每个 origin 调用一次，必须返回独立实例，由池负责释放；返回 `null` 在首次请求该 origin 时抛 `InvalidOperationException`。
- 注入 Handler 后 `client.Pool` 为 `null`、`UsesConnectionPool` 为 `false`；连接级选项（`Transport.MaxRedirects` / `WithCredentials` / `Proxy` / `Credentials`）改由 Handler 决定，在 defaults 上设置抛 `ERR_BAD_OPTION_VALUE`。
- 超时、进度、限速、大小上限等数据层能力不受影响。

### 可观测性

三路观测点默认开启，名字统一 `AxiosNet`。

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(AxiosTelemetry.ActivitySourceName))   // "AxiosNet"
    .WithMetrics(m => m
        .AddMeter(AxiosTelemetry.MeterName)                             // "AxiosNet"
        .AddView(AxiosTelemetry.RequestDurationInstrumentName,
            new ExplicitBucketHistogramConfiguration
            {
                // 耗时直方图按秒，OTel 默认边界是按毫秒设计的，必须自己配
                Boundaries = new[] { 0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10 }
            }));
```

**Trace**：一个逻辑请求一个 span（重试不另开），span 名取 HTTP 方法。

| 标签 | 说明 |
| --- | --- |
| `http.request.method` / `url.scheme` / `server.address` / `server.port` / `url.path` | 目标与方法 |
| `url.full` | 完整 URL（去 userinfo）；默认只到 path（`Observability.Capture = TelemetryCapture.Path`），`TelemetryCapture.Query` 带查询串，`TelemetryCapture.NoUrl` 不写 URL，`TelemetryCapture.Off` 完全不产出 |
| `http.response.status_code` / `network.protocol.version` | 状态码 / 真正用到的协议版本 |
| `http.request.body.size` / `http.response.body.size` | 字节数（未知不写） |
| `http.request.resend_count` | 已重试次数 |
| `error.type` / `axiosnet.error.code` | 状态码 → 错误码 → 异常类型名；以及库错误码 |
| `axiosnet.pool.endpoint.reused` | 是否命中池里已有 origin 节点（`false` = 顺手新建） |
| `axiosnet.cache.revalidated` | 这次响应由条件请求的 304 合并出来（正文来自缓存） |

- 4xx / 5xx 即使被 `validateStatus` 接受也标 `Error`；失败另记一条 `exception` 事件。
- 每次决定重试加一条 `retry` 事件，带 `axiosnet.retry.delay_ms` / `axiosnet.retry.reason` 与 `server.address` / `server.port` —— 与 span / 指标同一份逻辑地址。事件自带这些字段，是为了只看事件的消费方不必回头拼 span 标签。
- 换端点 / 对冲分支实际连的主机不在标签里，要看那条分支的尝试事件 —— `Attempt.Audit` 默认就在链上，订阅 `AxiosNet.Attempt*` 即可。
- 失败但收到了响应（4xx / 5xx）时照常写 `http.response.status_code` / `network.protocol.version` / `http.response.body.size`。
- `ResponseType.Stream` 的 span 在拿到响应头时结束，耗时等于首包时间。

**Metrics**（源名 `AxiosNet`）：

| 指标 | 类型 | 单位 | 标签 |
| --- | --- | --- | --- |
| `axiosnet.client.request.duration` | `Histogram<double>` | `s` | method / server.address / server.port / status_code / error.type |
| `axiosnet.client.active_requests` | `ObservableGauge<long>` | `{request}` | 无 |
| `axiosnet.client.request.retries` | `Counter<long>` | `{retry}` | method / server.address / server.port / error.type / `axiosnet.retry.reason` |
| `axiosnet.client.retry.budget_exhausted` | `Counter<long>` | `{retry}` | 无 |
| `axiosnet.client.gate.queue_depth` | `ObservableGauge<long>` | `{request}` | 无 |
| `axiosnet.client.gate.rejections` | `Counter<long>` | `{request}` | `axiosnet.gate.reason`（`queue_timeout` / `queue_full`） |
| `axiosnet.client.circuit.state` | `ObservableGauge<long>` | `{circuit}` | `axiosnet.circuit.state`（`closed` / `open` / `half_open`） |
| `axiosnet.client.circuit.untracked_host` | `Counter<long>` | `{host}` | 无 |
| `axiosnet.client.pool.leases` | `Counter<long>` | `{lease}` | `axiosnet.pool.endpoint.reused` |
| `axiosnet.client.pool.endpoints.created` | `Counter<long>` | `{endpoint}` | 无 |
| `axiosnet.client.pool.endpoints.evicted` | `Counter<long>` | `{endpoint}` | `axiosnet.pool.eviction.reason` |
| `axiosnet.client.pool.active_leases` | `ObservableGauge<long>` | `{lease}` | 无 |
| `axiosnet.client.pool.active_pools` | `ObservableGauge<long>` | `{pool}` | 无 |
| `axiosnet.client.pool.shared_pools` | `ObservableGauge<long>` | `{pool}` | 无 |
| `axiosnet.client.cache.hits` | `Counter<long>` | `{request}` | 无 |
| `axiosnet.client.cache.misses` | `Counter<long>` | `{request}` | 无 |
| `axiosnet.client.cache.stores` | `Counter<long>` | `{entry}` | 无 |
| `axiosnet.client.cache.store_failures` | `Counter<long>` | `{entry}` | 无 |
| `axiosnet.client.cache.revalidations` | `Counter<long>` | `{request}` | 无 |

- 缓存命中率 = `cache.hits / (cache.hits + cache.misses)`（跟随者拿到共享结果计命中；请求侧 `no-cache` / `no-store` 绕开新鲜查询的请求两边都不计，`no-cache` 走成条件请求时只计入 `cache.revalidations`；条件请求拿到 304 计一次未命中）；`cache.store_failures` 单独看，别混进命中率。
- `cache.stores` 记的是「写入完成」，不是「条目已落库」：内置存储容量满时会静默拒收新键（文档化策略，见 `MemoryCacheStore`），这次写入**仍然计入** `cache.stores`；`cache.store_failures` 只统计存储抛错、写超时与总预算耗尽。命中率与 `stores` 对不上时，先看容量上限。
- 指标只用低基数标签；URL 路径只进 span。**池指标不带 host / port**：网关 / 爬虫 / 多租户场景下一个 host 一条时间序列会把后端打爆；要定位到具体 origin 看 span 的 `server.address` / `server.port` 与 `axiosnet.pool.endpoint.reused`。
- `axiosnet.retry.reason` 取值有界：有状态码统一报 `status`（具体码在 `error.type`），否则是库错误码（`ECONNABORTED` / `ERR_NETWORK` …）——「该调策略（429）」和「该修下游（5xx）」在面板上直接分得开。
- `gate.queue_depth` 是此刻正在等槽位的请求数（进程级）；`gate.rejections` 是入口拒绝计数（排队超时 / 队列满）；`circuit.state` 是「多少个熔断维度处于该状态」（熔断器实例 × key）；`circuit.untracked_host` 是「状态表满员后按闭合态直通」的次数；`retry.budget_exhausted` 是「想重试但被共享预算拦下」的次数。
- 缓存五件套（`cache.hits` / `misses` / `stores` / `store_failures` / `revalidations`）同样不带标签 —— 上面这些都不带 host。
- `request.duration` 是逻辑请求耗时（含重试、退避与缓存落库等待）；`active_requests` 请求结束即减（流式到响应头），`active_leases` 要等正文流释放——「长下载占了多少并发额度」看后者。
- 池域指标描述池这个资源，不受 `Observability.Capture = TelemetryCapture.Off` 影响。
- `pool.active_pools` 是存活池数（建 +1 / `Dispose` -1），`pool.shared_pools` 是命名池表的条目数：前者持续上涨 = 有代码在反复建池（典型是「按请求 new 客户端」）。

**Events**（`DiagnosticListener` 名 `AxiosNet.DiagnosticListener`）：`AxiosNet.RequestStart` / `RequestStop` / `RequestFailed` / `Retry`，载荷是公开类型（`AxiosRequestStartEvent` / `StopEvent` / `FailedEvent` / `AxiosRetryContext`）。

**尝试审计**（`AxiosNet.AttemptStop` / `AxiosNet.AttemptFailed`，载荷 `AxiosAttemptEvent`）**默认就在链上**（`Attempt.Audit` 是默认链第 6 项，闸门之后、终端之前），订阅即可：

```csharp
// 默认链已包含 Attempt.Audit；自定义链时显式列出它
var pipeline = AxiosAttemptPipeline.Create(
    Attempt.Retry, Attempt.Failover, Attempt.Circuit, Attempt.Hedging, Attempt.Gate, Attempt.Audit);
using var client = Axios.Create(pipeline: pipeline);
```

- 覆盖**真正到达发送的尝试**：熔断拒绝、闸门排队超时、短路响应都不产生尝试事件（审计在它们之内）。
- 事件条数是「**端点 × 分支**」：换端点每换一个一条（`AttemptNumber` 不变，靠 `EndpointIndex` 区分），对冲每条分支一条；被胜出者取消的败者不发失败事件（那是清理，不是下游故障），在胜者出现之前就真实失败的败者照常发。
- 没人订阅时零成本直通：不建状态机、不取时间戳。
- 再 `Append(Attempt.Audit)` 会作为重复内置项被拒绝（每次尝试发两条事件只会污染口径）。

**按请求关闭或裁剪**：

```csharp
await client.GetAsync<object>("health", c => c.Observability.Capture = TelemetryCapture.Off);      // 完全不产出
await client.GetAsync<object>("api",    c => c.Observability.Capture = TelemetryCapture.NoUrl);     // 不写 URL
await client.GetAsync<object>("api?token=...", c => c.Observability.Capture = TelemetryCapture.Query);   // 把查询串也写进 url.full
```

## 治理

`Resilience` 子对象集中放重试、熔断、并发对冲、备用端点、并发闸门、幂等键与总预算；默认全部关闭，挂上策略才生效。

### 重试

默认不重试，挂策略即启用：

```csharp
using AxiosNet.Retry;

var client = Axios.Create(c => c.Resilience.Retry.Policy = new RetryPolicy { MaxRetries = 3 });

config.Resilience.Retry.Policy = RetryPolicy.Constant(3, TimeSpan.FromSeconds(1));                                      // 固定间隔
config.Resilience.Retry.Policy = RetryPolicy.Exponential(3, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5));    // 指数退避

await client.GetAsync("orders", c => c.Resilience.Retry.Policy = null);   // 单次关掉实例默认策略（显式 null ≠ 未设置）

client.Defaults.Resilience.Retry.OnRetry = ctx => logger.LogWarning(
    "第 {Attempt}/{Max} 次失败（{Code}），{Delay}ms 后重试",
    ctx.Attempt, ctx.MaxRetries, ctx.ErrorCode, ctx.Delay.TotalMilliseconds);
```

`RetryPolicy` 默认判定（每项都可改）：

| 情况 | 默认 |
| --- | --- |
| `ERR_NETWORK` / `ECONNABORTED`（**已发出**后超时） | 重试 |
| `ECONNABORTED`（`Resilience.TotalTimeout` 在发出前耗尽，`IsTotalTimeoutExceeded`） | 不重试（预算是整条请求的，重发必然在同一处立刻失败） |
| `ECONNABORTED`（单次超时被 `Resilience.TotalTimeout` 压小，到点时预算已花完） | 不重试（对外仍报 `IsTotalTimeoutExceeded`） |
| 408 / 429 / 5xx | 重试（`RetryStatusCodes` 可换） |
| 其它 4xx、配置 / URL 错误 | 不重试 |
| `ERR_CANCELED` | 不重试 |
| POST / PATCH 等非幂等方法 | 不重试（`Resilience.AllowUnsafeRetry = true` 打开） |

库级强制不重试（自定义策略也改不掉）：`ERR_CANCELED` / `ERR_CIRCUIT_OPEN` / `ERR_QUEUE_TIMEOUT` / `ERR_QUEUE_FULL` / `ERR_BAD_OPTION_VALUE` / `Resilience.TotalTimeout` 已耗尽（`IsTotalTimeoutExceeded`） —— 这些情况下请求要么没发出，要么立刻重发只会撞同一个闸门，要么是管线里的配置 / 编程错误。

其它成员：`Delay` / `BackoffFactor` / `MaxDelay` / `Jitter`（默认 `true`，等待落在 50%–100%）/ `HonorRetryAfter`（默认 `true`，服务端给了 `Retry-After` 就按它等，仍受 `MaxDelay` 封顶）/ `RetryWhen`。

- `Timeout` 是**每次尝试**的预算（3 次重试 × 10s 最坏 40s）；要「整条请求」的上界用 `Resilience.TotalTimeout`，也可以在调用方用 `CancellationToken`。
- 拦截器、`transformRequest`、正文序列化只跑一次；响应拦截器看到最终结果。
- 正文必须可重放：可 seek 的流每次重试前自动拨回起点；不可 seek 的流、`StreamContent`（含 multipart 部件）与流式请求体（`IAsyncEnumerable`）在开启重试 / 备用端点时直接抛 `ERR_BAD_OPTION_VALUE`。
- 调用方自带的 `HttpContent` 按**白名单**放行：`ByteArrayContent`（含 `StringContent` / `FormUrlEncodedContent`）、`ReadOnlyMemoryContent`、`JsonContent`（每次从持有的值重新序列化），以及全部部件都可重放的 `MultipartContent`；其余自定义内容一律在**发送前**拒绝。
- 「长度已知」不等于「可重放」：一次性流包一层自定义内容照样只发得出一次。
- 自定义策略：实现 `IAxiosRetryPolicy`（`MaxRetries` / `ShouldRetry(ctx)` / `GetDelay(ctx)`），`ctx` 提供 `Attempt` / `MaxRetries` / `Exception` / `Response` / `StatusCode` / `ErrorCode` / `Delay` / `Config` / `Method`。
- 策略被同一实例的并发请求共享，必须线程安全（推荐无状态；需要跨请求共享状态时自行同步，内置的 `RetryBudgetPolicy` 就是这样）。

#### 全局重试预算（`RetryBudgetPolicy`）

逐请求的重试在下游整体故障时会跟着请求数放大：100 个并发请求各重试 3 次就是 300 次多余发送，足够把一次抖动压成一次雪崩。`RetryBudgetPolicy` 给任意策略套一层**实例级共享**的令牌桶，只限速重试、不动首发：

```csharp
// 容量 20、每秒回补 5：允许一次连发 20 次重试，长期稳态 5 次/秒
client.Defaults.Resilience.Retry.Policy = new RetryBudgetPolicy(capacity: 20, refillPerSecond: 5);

// 内层策略决定「该不该重试 / 等多久」，桶只决定「还有没有预算」
client.Defaults.Resilience.Retry.Policy = new RetryBudgetPolicy(
    RetryPolicy.Exponential(3, TimeSpan.FromMilliseconds(200)), capacity: 50, refillPerSecond: 10);

var budget = (RetryBudgetPolicy)client.Defaults.Resilience.Retry.Policy;
budget.OnBudgetExhausted = ctx => logger.LogWarning("重试预算耗尽，{Code} 直接上抛", ctx.ErrorCode);
budget.RefusedRetries;   // 累计被预算拦下的重试次数，随时可读
```

- **只拦重试，不拦首发**：桶空了只是「不再重试」，调用方拿到的是内层策略判定为可重试的那次**真实失败**，不会被预算改写成新错误。
- **只被「会发出」的重试消耗**：
  - 内层或库级说「不该重试」的失败（4xx、取消、闸门拒绝、次数已到顶等）根本不取令牌。
  - 总预算已耗尽、这次重试注定发不出去时，刚扣的令牌会立刻退回（只认内建 `RetryBudgetPolicy`，自定义装饰器包住它时识别不到）。
  - 已经放行、但退避睡不完剩余总预算的那一次同样会**退回令牌并直接报耗尽**（不再白等一段只为「下一次尝试立刻失败」）—— 共享桶不会被空扣，也就不会连累同实例的其它请求。
  - 放行之后才发生的意外（进管线后被闸门拦下）仍计一次：决定那一刻不可预知，多计只让预算更紧，不会放大流量；退避等待期间被取消同样退回令牌 —— 那一发也没发出去。
- **令牌管的是「重试轮次」，不是「发送次数」**：一轮重试若开了备用端点 / 对冲，实际发送量最多放大到 `(1 + 端点数) × (1 + MaxHedges)` 倍（失败会提前结束一轮、跳过的端点不发送，实际通常更小）；要按发送量估算容量，记得除以这个系数。首发不取令牌。
- **桶按策略对象共享**：`Resilience.Retry.Policy` 在配置克隆间是同一个引用，所以同一个客户端实例的所有请求共用一个桶；某次请求想单独用一个桶，就在那次请求里 `new RetryBudgetPolicy(...)`。
- 桶初始是满的；`refillPerSecond = 0` 表示「只靠初始容量、不随时间恢复」（适合批处理 / 测试）。
- 线程安全，检查路径不产生分配；`OnBudgetExhausted` 在锁外触发，回调里可以放心做日志 / IO。
- 被拒次数计入 `axiosnet.client.retry.budget_exhausted`（无标签，受 `Observability.Capture` 控制）；要按请求上下文处理就用 `OnBudgetExhausted` 回调。

### 熔断

重试解决「这一次失败了要不要再试」；熔断解决「下游整体不可用时还要不要试」。默认不熔断：

```csharp
using AxiosNet.Circuit;

var breaker = new CircuitBreaker
{
    FailureThreshold = 5,                       // 连续 5 次下游故障 → 打开
    WindowSize = 20,                            // 另看最近 20 次的失败率
    MinimumSamples = 10,                        // 样本不足 10 个时不判率
    FailureRateThreshold = 0.5,                 // 窗口内一半失败就打开
    BreakDuration = TimeSpan.FromSeconds(30),   // 冷却 30 秒后放探测请求
    HalfOpenAttempts = 1,                       // 慢启动起点：半开态先放 1 个在途
    ProbeTimeout = TimeSpan.FromSeconds(30),    // 探测 30 秒不回报就整轮作废、重新打开
    RecoverySuccesses = 3                       // 连续 3 次探测成功才全量放开
};

breaker.StateChanged += change => logger.LogWarning(
    "{Host} 熔断：{From} -> {To}", change.Host, change.Previous, change.Current);

var client = Axios.Create(c => c.Resilience.Circuit.Policy = breaker);

await client.GetAsync("health", c => c.Resilience.Circuit.Policy = null);   // 探针不该被熔断挡住
breaker.Reset();                                                 // 运维手工恢复
```

状态机：`Closed` →（连续失败达 `FailureThreshold`，**或**窗口内失败率达 `FailureRateThreshold`）→ `Open` →（冷却 `BreakDuration`）→ `HalfOpen` →（连续 `RecoverySuccesses` 次探测成功）→ `Closed`；期间任何探测失败**立刻**回 `Open` 并重新计时。

**两条打开规则并存**：连续失败与滚动窗口失败率取「或」——样本攒够看比率，攒不够时连续失败兜底。窗口是**按最近 N 次结果**算的，与时间无关（不记时间戳，也不需要定期清理），旧账靠覆盖自然衰减。

失败口径与重试门禁对齐（**取消 / 4xx 不计失败**）：

| 情况 | 计入失败？ |
| --- | --- |
| `ERR_NETWORK` / `ECONNABORTED`（尝试已发出后超时） | 计入 |
| 5xx | 计入 |
| 4xx（含 408 / 429） | 不计入（也不清零） |
| `ERR_CANCELED` | 不计入 |
| 配置 / URL / 序列化 / 反序列化错误 | 不计入 |
| `ECONNABORTED`（`Resilience.TotalTimeout` 在请求**发出前**耗尽） | 不计入（本地约束先到期，下游一个字节都没收到） |

- **「没发出」不算下游故障**：排队 / 入口处就耗尽 `Resilience.TotalTimeout` 而抛的 `ECONNABORTED`（`AxiosCircuitContext.TotalTimeoutExpiredBeforeSend`）不进失败计数。过载时排队变长足以让每条请求都撞上它——记成故障就等于「越拥堵越容易熔断健康的下游」，把局部排队放大成整条链路不可用。已经发出去之后的超时仍照常计入，那才是下游不响应。
- **按 origin 计数**：默认维度 `host:port`（`uri.Authority`）；`KeySelector` 可改成 `ctx => ctx.Uri.Host` 或按路由前缀隔离租户。
- **打开条件取「或」**：连续失败达 `FailureThreshold`，或窗口（`WindowSize`）内失败数 ≥ `ceil(样本数 × FailureRateThreshold)` 且样本数达 `MinimumSamples`。`WindowSize = 0` 表示只看连续失败；`MinimumSamples` 大于 `WindowSize` 会在发请求前报 `ERR_BAD_OPTION_VALUE`（那种配法永远触发不了）。
- **慢启动恢复**：半开态允许的在途探测数是 `HalfOpenAttempts + 已连续成功次数`，凑满 `RecoverySuccesses` 次才转闭合。默认 1 → 2 → 3 逐步放宽，避免下游刚缓过来就被打回满负荷；`RecoverySuccesses = 0`（或 1）退化成「探测一次成功即闭合」。
- 探测失败会清零进度并立刻重开；被忽略的结果（4xx / 取消 / 预算耗尽没发出）只归还名额、不影响进度，且只有真的领到名额的探测才推进度。
- 熔断打开前就已放行、冷却结束之后才回报的迟到结果按代次丢弃：它属于上一轮，不会把刚进入的半开打回打开，也不会替这一轮归还探测名额。
- **卡死的探测不会永久占住名额**：半开态的探测在 `ProbeTimeout`（默认 30 秒，`null` 关闭）内没有回报就视为卡死——整轮半开作废、重新打开并重新冷却，迟到的结果按代次丢弃。请求本身没配 `Timeout` 时，一个挂住的探测原本会让该维度永远停在半开；探测自身的超时预算更长时把本项调大（非正值报 `ERR_BAD_OPTION_VALUE`）。
- 被拒请求**不发出**：不建连、不占租约，直接抛 `ERR_CIRCUIT_OPEN`，`AxiosException.RetryAfter` 给建议等待。
- 与重试同时开：闸门在**每次尝试之前**，熔断打开后重试的下一轮会被立刻拦下；`ERR_CIRCUIT_OPEN` 本身不重试。
- 开着重试时**每次尝试各记一笔**：3 次重试 + 首败 = 4 笔。
- **流式响应只按响应头记账**：正文读取发生在记账之后，中途断流（`IOException` / 连接重置）会以 `ERR_NETWORK` 抛给调用方，但不回补熔断失败、也不会触发重试。需要把「正文完整性」纳入失败统计时请在消费方自行处理，或改用 `ResponseType.ArrayBuffer` 让整段正文进入记账范围。
- 状态表有上限（`MaxTrackedHosts`，默认 512，**硬上限**）：满员时先淘汰「丢了不改变判定」的维度（闭合态里优先丢窗口中没有失败样本的那条；连续失败数可能已被一次成功清零，但失败率台账仍然有效）；仍放不下就**不再登记**新维度，新维度按闭合态直通（不参与熔断判定、不记账），已有维度的判定不受影响——熔断保护失效总好过状态表无界增长。未登记的次数见 `circuit.untracked_host` 指标。
- 自定义策略：实现 `IAxiosCircuitPolicy`（`ShouldAllow(ctx)` / `GetRetryAfter(ctx)` / `Record(ctx)`）；`ctx` 提供 `Host` / `Uri` / `Config` / `Exception` / `StatusCode` / `ErrorCode` / `Method` / `IsSuccess` / `IsFailure` / `TotalTimeoutExpiredBeforeSend`。
- 策略**有状态**且被并发共享，需自己保证线程安全。用装饰器包住内置 `CircuitBreaker` 时拿不到准入票（接口上没有票号），迟到结果会按旧口径记账 —— 与 `RetryBudgetPolicy` 被装饰器包住时认不出退费同类的保守偏差。

### 并发对冲

重试与熔断解决「失败了怎么办」；对冲解决「还没失败但太慢了」。默认关闭，`MaxHedges` 是**额外**分支数：

```csharp
var client = Axios.Create(c =>
{
    c.Resilience.Hedging.MaxHedges = 1;                       // 首发 + 1 条对冲分支
    c.Resilience.Hedging.Delay = TimeSpan.FromMilliseconds(80); // 第 n 条分支的起跑延迟 = n × Delay（默认 80ms）
});

// 单次请求打开 / 关掉
await client.GetAsync("search", c => c.Resilience.Hedging.MaxHedges = 2);
await client.GetAsync("orders", c => c.Resilience.Hedging.MaxHedges = 0);
```

首发照常立刻发出，之后每隔 `Delay` 再放出下一条分支；**第一个成功返回的结果胜出**（不比较耗时，谁先回谁算赢），其余分支取消并收尾。所有分支都失败时把失败交给外层重试。

- **熔断在整组之外，只记一笔**：一次逻辑尝试 = 一次熔断记账，与重试口径一致。所以「一败一成」算成功，「全败」算一次失败，不会因为分支多而放大失败计数。
- **并发额度按 `1 + MaxHedges` 倍占用**：每条分支各自过闸门、各占一个槽位。`Resilience.Concurrency.MaxInFlight` 要按这个倍数预留 —— 留得太小、`QueueTimeout` 又是 `Zero` 时，分支会直接在闸门处 `ERR_QUEUE_TIMEOUT`，对冲静默失效（它只是延迟优化，不会让请求失败）。
- **败者收尾有等待上限**：胜出后最多等败者 2 秒收尾（不等就会在返回后继续占着连接租约与槽位）；超时后改由后台续延，等分支真正结束再释放。库内中间件都响应 `ctx.AttemptToken`，正常情况立即收尾；忽略它的自写中间件最多把调用方拖住 2 秒。调用方取消时不等收尾（全部转后台续延），取消即时生效。
- **`transformResponse` 与响应侧回调在每条分支上各跑一次**（含落败分支）：带副作用的转换请自行保证幂等，或关掉对冲。
- **以下请求按单次发送处理**，不降级也不报错：流式响应（败者的正文无人消费）、非幂等方法（默认 `Resilience.AllowUnsafeRetry = false`）、正文不能并发重放（`Stream` / multipart / 上传进度包装）、响应侧带共享状态（`OnDownloadProgress` 或 `MaxRate > 0` 时进度会报两份、限速翻倍）。
- 泛型入口照常对冲：每条分支各填一份自己的响应对象，胜出者的那份交给调用方，调用方从 `config.Response` 读到的也是胜出者的状态码与响应头。
- `MaxHedges` 上限 16：再多分支追不回延迟，只会把下游打满。

### 备用端点

当前端点「用不了」时换下一个端点重发同一份请求，顺序即优先级（冷却中的端点自动退到末尾）：

```csharp
var client = Axios.Create(c => c.Resilience.Failover.Endpoints = new[]
{
    new Uri("https://backup1.example.com"),        // 先试它
    new Uri("https://backup2.example.com/api")     // 还不行再试它（可带路径前缀）
});

// 单次关掉（显式 null / 空集合 = 不换端点）
await client.GetAsync("health", c => c.Resilience.Failover.Endpoints = null);
```

端点提供 **origin 与路径前缀**，请求自己的 path / query 原样接在后面。请求 `https://primary/v1/users?page=2` + 端点 `https://mirror/api` → `https://mirror/api/v1/users?page=2`（端点前缀是**加**在请求路径前面，不是替换它 —— 别把请求里已有的前缀再写一遍）。

| 情况 | 换端点？ |
| --- | --- |
| `ERR_NETWORK` / `ECONNABORTED`（**已发出**后超时） / `ERR_POOL_EXHAUSTED` | 换 |
| `ECONNABORTED`（`Resilience.TotalTimeout` 在请求**发出前**耗尽） | 不换 —— 预算是整条请求的墙钟时间，换端点不会让它变长（见下） |
| 5xx（`ERR_BAD_RESPONSE` 且状态码 ≥ 500） | 换 |
| `ERR_CIRCUIT_OPEN` | 换 —— 熔断按 `host:port` 独立成账，主端点开着挡不住备用端点 |
| 4xx、其它 `ERR_BAD_RESPONSE`（如 2xx 的畸形正文） | 不换（换镜像改变不了） |
| `ERR_QUEUE_TIMEOUT` / `ERR_QUEUE_FULL` | 不换（本机没抢到槽位 / 没排上队，与下游无关） |
| 配置 / 编程错误、取消 | 不换 |

- **预算耗尽不换端点**：与[熔断的失败口径](#熔断)同一条边界 —— `Resilience.TotalTimeout` 在发出前就耗尽时，换端点既不能让预算变长，又要在备用端点白走一遍管线（占一次熔断判定与闸门入口，然后必然在同一处立刻失败）。已经发出去之后的超时照常换端点，那才是「换个下游可能就好」。
- **端点健康状态跨请求记忆**：
  - 同一端点连续失败达 `FailureThreshold`（默认 2）后进入冷却（`Cooldown`，默认 30 秒，按失败次数翻倍、封顶 `MaxCooldown` 5 分钟）。
  - 冷却期内它不再作为首跳，主端点持续不可用时不必每条请求都先白撞一次。
  - 只有「换一个下游可能好」的故障计失败（与上表同源）；4xx / 取消 / 配置错误既不加也不减。
  - 冷却到期自动重新参与；**所有端点都在冷却时仍按原顺序逐个试** —— 健康表只决定先后，不会把请求挡住。
  - 设 `FailureThreshold = 0` 或 `Cooldown = TimeSpan.Zero` 关闭。
- **每次重试都会重新过一遍端点顺序**：端点表在重试之内，一轮换完所有端点才把第一条失败交给重试；健康记忆只影响这一轮的先后，不影响「都试过才算失败」。
- **换端点会重算 `Host`**：调用方显式设过的 `Host` 头在换端点时丢掉，由目标地址重新算 —— 留着会让镜像按原端点的虚拟主机路由（`HttpClient` 只认 `Headers.Host`，它优先于 URI authority）。要按端点用不同的 `Host`，请写尝试中间件按 `ctx.Uri` 设置（`config.Headers` 只应用在模板上）。
- **端点表不能重复**：两个端点拼出同一个 base URL（尾斜杠不算区别）会在发请求前抛 `ERR_BAD_OPTION_VALUE` —— 那只是把同一份请求对同一个地址多打一遍。
- **端点拼出来就是当前地址时跳过**：端点与请求同 origin 且**不带路径前缀**（典型是只写 `https://backup.example.com`）时，换过去等于原地重发，直接跳过继续下一个端点。
- **`Timeout` 是「每个端点每次发送」的预算**：与重试同口径，3 个端点 + 10s 最坏 30s；要整条请求的上界请设 `Resilience.TotalTimeout`（跨端点共享，见[超时与取消](#超时与取消)）。上传进度同样跨端点累计（与重试重发正文一致，不回跳）。
- **正文要能按顺序重放**：与重试同一份要求（流必须可 seek），不可 seek 的流与 `StreamContent` 在开启备用端点时直接抛 `ERR_BAD_OPTION_VALUE`。
- **流式响应只在响应头阶段能换端点**：正文交给调用方之后，中途断流既不换端点也不重试（与熔断「只按响应头记账」同口径）。
- **端点必须是绝对的 `http` / `https` 地址，且不能带 query / fragment**：端点只当 base URL 用，端点里的 query 与请求自己的 query 怎么拼没有明确规则，因此直接报 `ERR_BAD_OPTION_VALUE`（要让某个参数只出现在备用端点上，请用请求拦截器按 `ctx` 拼 URL）。
- **端点表赋值即快照**：之后改调用方自己的列表不影响已生效的配置；读回来的就是内部那一版，调用方不得修改。
- **换端点会重判 HTTP/3 护栏**：跑不了当前 HTTP 版本的端点（如明文镜像 + 严格 h3）直接跳过继续下一个，全部不可用时才抛 `ERR_BAD_OPTION_VALUE`。
- 每次换端点产生一条尝试事件，`AttemptNumber` 不变、`EndpointIndex` 递增（见[可观测性](#可观测性)）。

### 幂等键

一个**逻辑请求**一个键，写进请求头；重试 / 换端点 / 对冲的各次发送带的是同一个键，服务端因此只把它当作一次写操作。

```csharp
var client = Axios.Create(c => c.Resilience.Idempotency.Enabled = true);        // 默认关闭

await client.PostAsync("orders", order);                             // Idempotency-Key: <32 位 Guid>
await client.PostAsync("orders", order, c => c.Resilience.Idempotency.Key = "upstream-7f3a");  // 透传上游的键
```

- **默认关闭**：非幂等方法自动带键会把「重复写」变成「静默去重」，必须由你确认接口支持幂等键。
- **重试本身要先放行非幂等方法**：`RetryPolicy` 默认只重试幂等方法，键再好也不会让 POST 被重发 —— 需要同时打开 `Resilience.AllowUnsafeRetry`（见[重试](#重试)）。
- **键在请求模板上写一次**，模板被复制时随头一起搬走 —— 重试不会换键。`OnSendingRequest` 是**每次尝试**一次，在那里写键是错的（每次重试都会换一个键）。
- **一个逻辑请求一个键**：每调用一次 `RequestAsync` 都是新的生效配置、新键；认证失败后的重放要沿用原键，请把它写进 `Idempotency.Key` 再发。
- `Key`（显式值）优先于 `KeyFactory`；两者都没有时用 `Guid` 的 `N` 格式。
- `HeaderName` 默认 `Idempotency-Key`；已经显式写了同名头的请求不会被覆盖。
- 键工厂返回空白、头名非法都在发请求前抛 `ERR_BAD_OPTION_VALUE`。
- **对冲 + 幂等键**：两条分支同键，服务端去重后其中一条会拿到「已处理」的结果。这正是幂等键该有的行为，但要求服务端真的按键去重 —— 否则只是把同一份写操作发了两次。

### 尝试管线

「一次尝试」（单次发送）被抽象成可组合的中间件链，默认链是 **重试 → 备用端点 → 熔断 → 对冲 → 闸门 → 审计 → 发送**。新增每次尝试的能力插一个中间件即可，不必再往客户端里加分支。

```csharp
using AxiosNet.Pipeline;

// 插一个「每次尝试」的探针：Append 落在链尾（审计之内、发送之前），重试 N 次就调用 N+1 次
var pipeline = AxiosAttemptPipeline.Default.Append(next => async (request, ctx) =>
{
    var clock = Stopwatch.StartNew();
    try
    {
        return await next(request, ctx);
    }
    finally
    {
        logger.LogDebug("第 {Attempt} 次尝试耗时 {Ms}ms", ctx.AttemptNumber, clock.ElapsedMilliseconds);
    }
});

using var client = Axios.Create(c => c.Resilience.Retry.Policy = new RetryPolicy(2), pipeline: pipeline);
```

- **上下文 `AxiosAttemptContext` 是只读结构体**，字段：`Config` / `Uri` / `AttemptNumber` / `EndpointIndex` / `AttemptToken` / `ExternalToken` / `DataType` / `Target` / `StreamResponse` / `Retry` / `Circuit` / `Hedging` / `Failover` / `Slot`；只能用 `WithXxx` 派生副本。
- **两个编号分开走**：`AttemptNumber` 由重试推进，`EndpointIndex` 由备用端点推进（`0` = 请求自己的地址）。换端点不推进尝试号，所以同一个尝试号下可能有多条尝试事件。
- **中间件恒在链上，是否干活由它自己看 `ctx.Config` 决定**：没配重试策略时重试中间件就是纯直通。链因此可以在构造时固化，不必每次请求重建。
- **审计默认在链上**：`Attempt.Audit` 是默认链第 6 项（闸门之后、终端之前），订阅 `AxiosNet.Attempt` 就能拿到每次尝试的耗时 / 状态码 / 错误码，不订阅时只做一次开关判定、不建状态机。它已在链上，再 `Append(Attempt.Audit)` 会被当作重复内置项拒绝。`Append` 的自定义项排在它内侧（更靠近终端），因此它们的执行时间计入审计的事件耗时。
- **顺序是契约**：重试在最外层，于是每次尝试都会重过一遍备用端点、熔断、对冲与闸门；备用端点在熔断之外，于是每个端点各自过熔断；熔断在对冲之外，于是整组分支只记一笔；熔断排在闸门之前，被拒的请求不占槽位。
- **重排与摘除**：`AxiosAttemptPipeline.Create(...)` 显式列出全部中间件（第一个最外层），`Remove(Attempt.Gate)` 摘掉某项；不可变，每次编辑返回新实例。内置项重复出现会报错。
- **不提供「放到最外层」的入口**：那个位置每次请求只调用一次，与响应拦截器完全同构。`InsertBefore(Attempt.Retry, ...)` 会明确拒绝并指向 `client.Interceptors.Response`——它还有 `OnRejected`、运行时增删、覆盖短路三个额外好处。
- **并发槽位不用你管**：闸门自己兜底归还 —— 中间件不调 `next`（自己给一份响应）或自己抛异常，槽位也不会漏。唯一不让闸门归还的情况是**库内**的流式响应：槽位随正文交给 `StreamOwner`，由流的 `Dispose` 释放，正文下载期间该请求一直占着额度。中间件自建 `StreamOwner` 不算这一例外：闸门照常归还自己的槽位，自建凭证不占并发额度。
- **谁创建谁释放**：中间件自己 clone 的 `HttpRequestMessage` 由自己释放（模板仍归管线）。替换响应时必须处置旧的 `AxiosAttemptResult.StreamOwner`，或把它带进新结果（`new AxiosAttemptResult(newResponse, old.StreamOwner)`）—— 它持有连接租约与并发槽位，丢弃即泄漏。
- **中间件自身的耗时不计入 `Timeout`**：超时预算在终端内部起算，闸门排队与中间件前置逻辑都在它之外。
- 响应侧读请求头用 `response.GetRequestHeader(name)`（先查请求头、再查内容头）；短路响应没有底层请求消息，返回 `null`。

与其他扩展点的分工：

| 想做的事 | 用哪个 |
| --- | --- |
| 改 URL / 头 / 请求体、注入短路（一次请求一次） | 请求拦截器 |
| 在序列化前加工要发的 `Body` | `transformRequest` |
| 在发送前改写最终请求消息（签名、时间戳、幂等键，每次尝试） | `c.Observability.OnSendingRequest` |
| 在反序列化前拿原始响应字节（每次尝试） | `c.Observability.OnRawResponse` |
| 请求返回后仍要读正文原文 / 原始字节（记日志、排查） | `response.RawText`（错误体）/ `c.Observability.CaptureRawResponseBody` + `response.RawBody` |
| 在反序列化后加工回来的 `Data`（每次尝试） | `transformResponse` |
| 加工最终响应、把异常兜成响应 | 响应拦截器 |
| 观察或干预**每一次尝试**（含审计） | 尝试中间件 |
| 改「重试几次 / 退避多久 / 什么时候熔断 / 要不要对冲 / 换哪些端点」 | 对应的 `Policy` 或子配置，不换中间件 |

### 短路响应

有时不该发请求，但仍然需要一份「响应」——缓存命中、mock、离线回放、契约测试。设置 `c.ShortCircuit` 即可：本次请求**不发送**，直接交付这份结果。

```csharp
// 缓存命中：请求拦截器是天然的位置——它只能改配置，ShortCircuit 就是「直接给响应」的出口
client.Interceptors.Request.Use(config =>
{
    if (config.Url is { } url && cache.TryGetValue(url, out var entry))
    {
        config.ShortCircuit = new AxiosShortCircuit
        {
            Status = entry.Status,
            ContentType = entry.ContentType,                              // 含 charset，丢了会乱码
            Headers = new Dictionary<string, string> { ["X-Cache"] = "HIT" },
            Body = entry.Body                                            // 字节：按本次调用的类型重新解析
        };
    }

    return config;
});

// mock / 固定夹具：数据已经成型，原样交付
var response = await client.GetAsync<User>("users/1",
    c => c.ShortCircuit = AxiosShortCircuit.From(new User { Id = 1, Name = "stub" }));
```

跳过的都是「一次 HTTP 操作」本身，保留的是「响应方向」的语义：

| 阶段 | 短路时 |
| --- | --- |
| 并发闸门 / 排队、连接租约、发送、重试、熔断 | 不参与 |
| 遥测 span 与在途指标 | 不产生（命中率由你的缓存自己统计） |
| 范围校验（`Timeout` / 各项上限） | 照常（配置自身的错误不因命中而消失） |
| 按 `ResponseType` / 泛型参数反序列化、`transformResponse` | 照常 |
| `ValidateStatus`（非 2xx 抛 `AxiosException`）、响应拦截器 | 照常 |

- **两种载荷二选一**：`Body` 是原始字节（缓存用它——同一份字节喂给 `GetAsync<User>` 与 `GetAsync<string>` 应得到不同形态）；`Data` 是已成型结果（mock 用它，不解析、不跑 `transformResponse`，所有权归调用方）。
- **`Body` + `Stream` / `byte[]`**：流每次新建 `MemoryStream`，字节数组每次复制一份——两次调用拿到的永远是独立对象，缓存条目不会被某次写入污染。
- **只属于本次请求**：`AxiosConfig.Clone` 会清空它，所以放在 `Defaults` 上不生效；回放 `response.Config` 也不会重放上一次的短路结果。
- **取消仍然生效**：已取消的令牌直接抛 `ERR_CANCELED`，不会因为「正好命中」而返回成功。
- **响应头一并过闸**：`Headers` 的名字必须是合法 token、值不得含控制字符（与请求头同一把尺子），非法时抛 `ERR_BAD_OPTION_VALUE` —— 脏头一样会进日志、喂给 `transformResponse`。
- 可辨痕迹只有 `response.Request == null`（没有发出过请求）；`ProtocolVersion` 不替你编，默认 `null`。
- 缓存条目最少要存 `Status` / `ContentType` / `Body` 三样；响应头的 `Content-Type` 决定文本解码，丢了字符集会让非 UTF-8 正文在命中时乱码。

## 声明式客户端

端点一多，URL、参数、请求头散落在每个调用处就难管。声明式入口把每个端点写成**请求模型**或**接口**，调用只剩一句 `client.RequestAsync(request)` / `api.GetAsync(5)`；两种写法由同一个客户端承载：`client.Create<T>()` / `client.RequestAsync(model)` 是 `AxiosNet.WebAPI` 扩展方法；需要 `CredentialProvider` / `ErrorFactory` 时改用 `ApiClient`。命名空间 `AxiosNet.WebAPI`（特性在 `AxiosNet.WebAPI.Attributes`），与核心同包。

| 写法 | 长什么样 | 适合 | 代价 |
| --- | --- | --- | --- |
| 请求模型（默认推荐） | `class GetUserRequest : IApiRequest<User>`，属性上标 `[Path]` / `[Query]` / `[Body]` | 端点相对独立，请求要当对象传递或复用 | 每个端点一个类型 |
| 接口声明 | `interface IUserApi`，路由与绑定写在方法 / 参数上 | 端点成组，调用像本地方法 | 源生成器烘焙真实代理（JIT / Native AOT 同一路径）；未生成时回退 `DispatchProxy`（仅 JIT） |

两种写法共用同一套内核：路径模板校验、重名检测、默认 Body 推断、标量护栏、静态头、授权语义全部一致。模型式是默认推荐，先看它。

### 模型式 API

默认推荐：一个端点一个类。路由写在类上、绑定特性写在属性上，URL 由声明拼出，调用只剩 `RequestAsync` / `GetUri` / `RequestStreamAsync`；返回类型由 `IApiRequest<TResponse>` 的泛型决定。

```csharp
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;

[Get("users/{id}")]
[Headers("Accept: application/json")]                    // 类级静态头
public sealed class GetUserRequest : IApiRequest<User>
{
    [Path] public int Id { get; set; }
    [Query("verbose")] public string? Verbose { get; set; }
    [Header("X-Trace")] public string? Trace { get; set; }
}

[Post("users")]
public sealed class CreateUserRequest : IApiRequest<User>
{
    [Body] public UserPayload Payload { get; set; } = null!;
}

var client = new ApiClient("https://api.example.com/")
{
    CredentialProvider = CredentialProviders.Fixed("token")
};

var user = (await client.RequestAsync(new GetUserRequest { Id = 5 })).Data!;   // User
string url = client.GetUri(new GetUserRequest { Id = 5 });                     // 只拼 URL
```

**调用与返回**

- `IApiRequest<TResponse>` 的 `TResponse` 就是 `Data` 的类型：`RequestAsync` 返回 `AxiosResponse<TResponse>`，`GetUri` 只拼 URL、不发请求。
- `client.RequestAsync(model)` / `RequestStreamAsync` 是 `AxiosClient` 上的扩展方法（`using AxiosNet.WebAPI;`），与 `new ApiClient(...)` 共用同一个门面、`CredentialProvider` / `ErrorFactory` 状态。
- 按请求微调：`RequestAsync(request, config => ...)`，或继承 `ApiRequest<TResponse>` 覆写 `Configure(config)`；回调在参数绑定之后、授权之前。
- `client.Axios` 直接进底层 `AxiosClient`（拦截器、池、Handler 全可用）。

**绑定**

| 绑定 | 特性 | 说明 |
| --- | --- | --- |
| 路径段 | `[Path]` | 替换 `{name}`，值 URL 转义，`null` 报错 |
| 查询参数 | `[Query]` / 无标注属性 | 默认绑 Query；数组 `a[]=v`、嵌套 `a[b]=v` |
| 字典查询 | `[Query]` + 字典 | 未命名平铺 `?a=1&b=2`，显式命名保留 `?filters[a]=1` |
| 请求头 | `[Header]` / `[HeaderCollection]` | `null` 跳过 |
| 静态头 | `[Headers]`（类 / 接口 / 接口方法） | `"Name: Value"`；只写名字表示删除该头（同名 `[Header]` 属性 / 参数仍会把它写回来）；方法级覆盖接口级同名项 |
| 表单 | `[Multipart]` + `[Part]` | 模型自身即表单；`[Part]` 覆盖字段名 / 文件名 / 部件类型 |
| 请求体 | `[Body]` | 每个模型最多一个；`null` 不发请求体 |
| 值格式化 | `[Format]`（属性 / 参数） | 格式串或静态函数；集合逐元素、字典逐值（见[值格式化](#值格式化)） |
| 编码 | `[BodyContentType]` / `[QueryCollectionFormat]` / `[Stream]`（类 / 属性） | 属性级覆盖类级；两处都显式声明且值不同报 `ERR_BAD_OPTION_VALUE`；`[Multipart]` 与 `[BodyContentType]` 互斥 |
| 排除 | `[ApiIgnore]` | 完全不绑定 |

- 单负载模型可省 `[Body]`：POST / PUT / PATCH 且模型只有一个未标注的**非标量**属性时默认按 Body 绑定（字典属性除外，始终按查询串）。
- 一个属性可同时标多个绑定特性（`[Path]` + `[Header]`）：值只读一次、分别应用。
- 值格式化：成员上的 `[Format]` 优先，其余走实例默认值 `AxiosConfig.Serialization.ValueText`；声明式必须在实例默认值上设置，在按请求 `configure` 里改会抛 `ERR_BAD_OPTION_VALUE`（见[值格式化](#值格式化)）。

**编码与流式**

编码与帧格式既能按请求配置，也能直接声明在模型上：

```csharp
[Post("login")]
[BodyContentType("application/x-www-form-urlencoded")]   // 类级：该模型默认按表单编码
public sealed class LoginRequest : IApiRequest<Token>
{
    [Body] public LoginForm Form { get; set; } = null!;
}

[Get("users")]
public sealed class ListUsersRequest : IApiRequest<User[]>
{
    [Query]
    [QueryCollectionFormat(AxiosCollectionFormat.Multi)]  // 属性级：只作用于该查询属性
    public int[] Ids { get; set; } = new int[0];
}

[Get("stream/events")]
[Stream(AxiosStreamFormat.ServerSentEvents)]              // 类级默认帧格式；省略按 NDJSON
public sealed class StreamEventsRequest : IApiRequest<Chunk> { }
```

- `[BodyContentType(...)]` 可标在模型类或属性上，写入 `config.ContentType`（决定正文编码，不只是加一个头）；`[QueryCollectionFormat(...)]` 同理写入 `config.Serialization.CollectionFormat`。
- 优先级：只有一处声明时该处生效；属性级覆盖类级；**两处都显式声明且值不同**时构建期抛 `ERR_BAD_OPTION_VALUE`，不会静默取一个。
- `[Multipart]` 与 `[BodyContentType]` 同时存在报错（multipart 的媒体类型固定为 `multipart/form-data`）。
- 流式：`[Stream(...)]` 可标在模型类或属性上作为默认帧格式，也可继续用 `RequestStreamAsync(request, format)` 显式传格式（显式参数优先）；`IApiRequest<TItem>` 的泛型就是单个帧的类型。OpenAPI 生成的模型另带 `public const AxiosStreamFormat StreamFormat`，可直接传 `XxxRequest.StreamFormat`。
- 仍可逐请求微调：`RequestAsync(request, c => c.ContentType = ...)`、`c.Serialization.CollectionFormat = AxiosCollectionFormat.Multi`，或继承 `ApiRequest<T>` 在 `Configure` 里写死。
- 正文超时跟随客户端的 `Timeout` / `Resilience.TotalTimeout`，没有独立的空闲超时旋钮；帧解析失败为 `ERR_BAD_RESPONSE`、中途断流为 `ERR_NETWORK`、取消为 `ERR_CANCELED`。

**校验与报错**

- 模型定义非法（缺路由、占位符未绑定、多个 Body…）首次调用抛 `InvalidOperationException`。
- 启动期体检用 `ApiClient.ValidateModels(typeof(A), ...)` 或 `ValidateModels(assembly)`，返回全部错误消息、不抛：
  - 按类型重载在 Native AOT 下同样可用（已生成的声明直接走注册表）。
  - `ValidateModels(assembly)` 依赖反射枚举，AOT 下改调生成的 `AxiosNet.Generated.AxiosNetValidation_<程序集名>.ValidateAll()`。
- 声明本身合法、但运行时取值 / 格式化失败（路径值为 `null`、格式化函数抛错、非法头名 / 头值等）属于请求装配失败，统一抛 `AxiosException`（`ERR_BAD_REQUEST`），原异常在 `Cause`；与正文 / multipart 装配同口径。

**授权与强类型错误**

- 授权：`[Authorize]` / `[Authorize("Basic")]` 声明需要授权，`[AllowAnonymous]` 跳过整条链路；唯一扩展点是 `ApiClient.CredentialProvider`（`IApiCredentialProvider`），返回的 `ApiCredential` 决定落在哪里（`Authorization` 头 / 任意请求头 / 查询参数，见[凭据与令牌管理](#凭据与令牌管理)）。
- 强类型错误：`client.ErrorFactory = new ApiErrorFactory<ProblemDetails>()` 后状态码错误抛 `ApiError<TError>`（网络 / 超时 / 取消不经过工厂）。`ApiError<TError>` 继承自 `AxiosException`，只写 `catch (AxiosException)` 也能接住；`Code` / `ErrorCode` / `Config` / `Request` / `ToJson()` 都从基类继承。

**Native AOT**

- 模型所在程序集启用包内源生成器，成员取值委托在编译期烘焙（内部类型同样支持），AOT 下不反射；未生成 / 未注册时 `RequestAsync` 抛 `AxiosAotMetadataException`（带修复指引）。
- 启动期按类型校验 `ApiClient.ValidateModels(typeof(A), ...)` 在 AOT 下可用；程序集扫描不可用，改调生成的 `ValidateAll()`。
- 生成路径不要求 `TrimmerRootAssembly`。

### 接口式 API

不想为每个端点写请求模型时，可直接把接口交给 `client.Create<T>()`：路由写在方法上、绑定特性写在参数上。启用包内源生成器时，代理类在编译期生成（JIT / Native AOT 同一路径）；未生成时才回退 `DispatchProxy`（仅 JIT）。

```csharp
[Headers("Accept: application/json")]                    // 接口级静态头
public interface IUserApi
{
    [Get("users/{id}")]
    Task<AxiosResponse<User>> GetAsync(
        [Path] int id,
        [Query("verbose")] string? verbose = null,
        [Header("X-Trace")] string? trace = null,
        CancellationToken cancellationToken = default);

    [Post("users")]
    Task<AxiosResponse<User>> CreateAsync(UserPayload payload, CancellationToken cancellationToken = default);
}

var api = client.Create<IUserApi>();
var user = (await api.GetAsync(5)).Data!;                 // User
```

**调用与返回**

- 每个方法必须标注且只标注一个路由特性。返回类型有四种：`Task<AxiosResponse<T>>`（完整响应）/ `Task<AxiosResponse>`（正文按 `JsonElement` 原样交付）/ `Task<T>`（只交付 `Data`）/ `Task`（只完成请求，不解析正文——非 JSON 正文不会变成解析错误）。
- 返回 `Task<AxiosResponse<IAsyncEnumerable<T>>>` 即流式方法；裸 `Task<IAsyncEnumerable<T>>` 缺少响应元数据与释放归属，启动期校验即报错。
- 不支持同步返回 / `ValueTask` / 泛型方法 / `ref`·`out`。
- 四种形态的失败口径一致：非 2xx 抛 `AxiosException`，配置了 `ErrorFactory` 时照常替换/包装；用 `Task` 形态时 `ErrorFactory` 仍能拿到响应（只是正文被丢弃）。

**绑定**

- `CancellationToken` 参数按类型识别（最多一个），不参与绑定。
- 其余参数按 `[Path]` / `[Query]` / `[Header]` / `[HeaderCollection]` / `[Part]` / `[Body]` / `[ApiIgnore]` 或「未标注」规则处理，默认 Body 推断同样生效。
- `[Multipart]` 标在方法上：参数即表单部件，`[Part]` 覆盖字段名 / 文件名 / 部件类型。
- `[Format]` 可标在参数上；其余走实例默认值 `AxiosConfig.Serialization.ValueText`（见[值格式化](#值格式化)）。

**编码与流式**

multipart 与流式同样直接写在接口上（语义与请求模型一一对应）：

```csharp
[Post("upload")]
[Multipart]                                              // 参数即表单
Task<AxiosResponse<UploadResult>> UploadAsync(
    [Part("Title")] string title,
    [Part(FileName = "data.bin")] byte[] blob,
    CancellationToken cancellationToken = default);

[Get("stream/events")]
[Stream(AxiosStreamFormat.ServerSentEvents)]            // 省略 = NDJSON
Task<AxiosResponse<IAsyncEnumerable<Chunk>>> EventsAsync(CancellationToken cancellationToken = default);
```

编码声明同样是方法级的——媒体类型决定正文怎么编码，查询集合格式决定数组怎么写进 URL：

```csharp
[Post("login")]
[BodyContentType("application/x-www-form-urlencoded")]   // 对象按表单展平（只写 Content-Type 头不会改变编码）
Task<AxiosResponse<Token>> LoginAsync([Body] LoginForm form, CancellationToken cancellationToken = default);

[Get("users")]
[QueryCollectionFormat(AxiosCollectionFormat.Multi)]    // ids=1&ids=2（默认 Brackets：ids[]=1&ids[]=2）
Task<AxiosResponse<User[]>> ListAsync([Query] int[] ids, CancellationToken cancellationToken = default);
```

- `[BodyContentType(...)]` 写入 `config.ContentType`（决定正文编码，而不是只加一个头），用于表单、XML 或自定义媒体类型；它与 `[Multipart]` 互斥（multipart 的媒体类型固定）。
- `[QueryCollectionFormat(...)]` 写入 `config.Serialization.CollectionFormat`，覆盖实例默认值；OpenAPI 的 `explode: true` 对应 `AxiosCollectionFormat.Multi`。
- 接口级流式：帧格式用 `[Stream(AxiosStreamFormat.ServerSentEvents)]` / `[Stream(AxiosStreamFormat.JsonArray)]` 声明，省略按 NDJSON；返回流只能枚举一次，超时与错误口径与 `RequestStreamAsync(...)` 完全一致。
- 接口式仍只使用方法级声明（与之前一致）；`[BodyContentType]` / `[QueryCollectionFormat]` / `[Stream]` 新增的类 / 属性作用域属于模型式，优先级与冲突规则见[模型式](#模型式-api)的「编码与流式」。
- 接口方法不支持方法级 `configure` 钩子，需要逐请求微调时用请求模型。

**校验与报错**

- 接口写在非法（缺路由、多个 Body、`[Part]` 未配 `[Multipart]`、`[Multipart]` 与 `[Body]` / `[BodyContentType]` 同用、`[Stream]` 用在非流式方法、参数重名…）时，`Create<T>()` 抛 `InvalidOperationException`。
- 启动期体检用 `ApiClient.ValidateInterfaces(typeof(IA), ...)` 或 `ValidateInterfaces(assembly)`：返回全部错误消息、不抛，同一接口的多个方法错误会聚合而不是只报第一条。
  - 按类型重载在 Native AOT 下同样可用。
  - `ValidateInterfaces(assembly)` 在 AOT 下抛 `PlatformNotSupportedException`，改调生成的 `ValidateAll()`。
- 声明式装配失败（路径 / 查询 / 请求头 / 正文的取值或格式化抛错）统一为 `ERR_BAD_REQUEST`，原异常在 `Cause`。

**授权与强类型错误**

- `[Headers]` / `[Authorize]` / `[AllowAnonymous]` 可标在接口或接口方法上；继承基接口时，静态头与授权按**当前派生接口作用域**合并——同一基方法在不同派生接口下可以有不同静态头。
- 方法级声明压过接口级：静态头同名项覆盖、`[AllowAnonymous]` 可让接口里单个方法匿名、`[Authorize("Basic")]` 可让单个方法换 scheme。
- 方法上同标 `[Authorize]` 与 `[AllowAnonymous]` 属二义性（`Create<T>()` / `ValidateInterfaces` 报错）。
- 强类型错误与模型式相同：`client.ErrorFactory = new ApiErrorFactory<ProblemDetails>()` 后状态码错误抛 `ApiError<TError>`（网络 / 超时 / 取消不经过工厂）。
- 凭据提供方（`ApiAuthContext.Request`）与错误工厂拿到的是 `ApiInterfaceInvocation`（接口类型、方法名、参数快照），不是模型实例；需要按请求分支时用这三项。

**Native AOT**

- 接口所在程序集启用源生成器即生成真实代理（内部接口同样支持）；闭合泛型接口（如 `IRepo<User>`）在调用点 `Create<T>()` / `AddApiInterface<T>()` 出现时生成，只有开放泛型声明本身会给出 `AXA011` 警告。
- 未生成 / 未注册时 AOT 下 `Create<T>()` 抛 `AxiosAotMetadataException`（带修复指引），JIT 下回退 `DispatchProxy`（需 public 接口）。
- 生成路径不要求 `TrimmerRootAssembly`，也不再需要 root 接口类型。

### 值格式化

值写进 URL、请求头或表单时最终都是文本。库把「值 → 文本」收敛成**一条**路径，两层声明按优先级叠加：

1. 成员上的 `[Format]`（模型属性 / 接口参数）—— 最具体；
2. 客户端实例默认值上的 `AxiosConfig.Serialization.ValueText`（直接调用时也可在单次请求的配置里给）—— 成员没声明时生效；回调带 `ValueTextChannel`，可以只处理某个通道；
3. 通道默认规则（日期时间折 UTC 毫秒、枚举取名字、`byte[]` 取 base64、`IFormattable` 按不变文化…）。

```csharp
[Get("users/{id}")]
public sealed class ListRequest : IApiRequest<User[]>
{
    [Path, Format("0000")] public int Id { get; set; }                  // /users/0002
    [Query, Format("yyyy-MM-dd")] public DateTime Since { get; set; }   // since=2024-01-02
    [Query, Format("X2")] public int[] Codes { get; set; } = [];        // codes[]=0B&codes[]=FF

    [Query("amounts"), Format(typeof(Formats), nameof(Formats.Money))]  // amounts[min]=12.50
    public Dictionary<string, decimal> Amounts { get; set; } = new();
}

public static class Formats
{
    // 签名固定：public static string? M(object? value, ValueTextChannel channel)；
    // 返回 null = 本次不处理，回落默认规则；不关心通道时把第二个参数写成 _ 丢掉即可。
    public static string? Money(object? value, ValueTextChannel _)
        => value is decimal amount ? amount.ToString("0.00", CultureInfo.InvariantCulture) : null;
}

var client = new ApiClient(url, c =>
{
    // 第二个参数是值所在通道（AxiosNet.Metadata.ValueTextChannel）：
    // 请求头里用 RFC 1123，其余位置用紧凑日期；返回 null 表示本次不处理，回落默认规则。
    c.Serialization.ValueText = (value, channel) => value is DateTime date
        ? channel == ValueTextChannel.Header
            ? date.ToString("R", CultureInfo.InvariantCulture)
            : date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
        : null;
});
```

- **覆盖的通道**：路径段 / 查询串 / 请求头 / 表单字段 / multipart 文本部件 / 裸标量正文。同一套规则，不存在「查询里能格式化、表单里不能」；钩子的第二个参数就是这六种之一（`Path` / `Query` / `Header` / `Form` / `Multipart` / `ScalarBody`），用来按位置分叉。
  - 请求头集合（`[HeaderCollection]`）的字典值逐项经过钩子（通道 `Header`），与单值 `[Header]` 同一口径；`[Format]` 仍不能标在该成员上（值本就是字符串，构建期报错）。
- **成员函数也拿得到通道**：`[Format(typeof(Formats), nameof(Formats.Money))]` 指向的静态方法签名固定为 `public static string? Money(object? value, ValueTextChannel channel)`，第二个参数是该成员所在的通道；不关心通道就写成 `_` 丢弃，没有「只写一个参数」的形态。
  - 裸标量正文（`data` 本身是标量）含 `byte[]` 等二进制形态：只有声明 / 钩子真的产出文本时才改成文本正文，否则按原始字节发送，钩子返回 `null` 不会触发 base64。
  - 直接调用（`PostAsync(url, data)`）的裸标量同样过钩子；但 `data` 是 `string` / `byte[]` / `Stream` / 字节块时属于「原样正文」，没有「值 → 文本」这一步，钩子不参与。
  - 查询里的嵌套对象 / 字典 / `JsonElement` 树按叶子应用：成员声明优先，其次实例钩子。
- **集合与字典**：标在集合上逐元素应用，标在字典上逐值应用（键不动）。字典上的声明不做构建期类型校验——值类型运行期才知道。
- **格式串**要求值实现 `IFormattable`，区域固定为不变文化（不跟随 `CurrentCulture`）；需要区域相关的形态就写格式化函数。
- **结构化正文内部不归它管**：`[Body]` + 结构化类型上标 `[Format]` 在构建期报错（文档内部由序列化器负责）。要让 JSON 里的成员也按 `[Format]` 输出，挂可选桥：

```csharp
using AxiosNet.WebAPI.Serialization;

// net7.0+，Native AOT 下不可用（桥按属性类型构造泛型转换器）
config.Serialization.JsonOptions =
    AxiosJsonFormat.ApplyTo(new JsonSerializerOptions(JsonSerializerDefaults.Web));
```

- **构建期报错**，不留到运行期静默回落（模型属性与接口参数走同一套校验）：
  - `[Format]` 与结构化 `[Body]` / `[HeaderCollection]` / 文件部件（`Stream` / `byte[]` / `Memory<byte>` / `ReadOnlyMemory<byte>` / `ArraySegment<byte>` / `FormFile` / `HttpContent`，集合元素 / 字典值同此判定）同用；
  - 格式串用在非 `IFormattable` 的标量（如 `bool`）或二进制形态（`byte[]` / `Memory<byte>` / `ReadOnlyMemory<byte>` / `ArraySegment<byte>`）上；
  - 标在非标量（POCO）的查询参数上。
- **写错怎么发现**，三种都不必等到发请求：
  - 方法名写成 `nameof(...)` 时拼错是普通编译错误（`CS0117`）；
  - 签名不对（非 `public static` / 泛型 / 参数列表不是 `(object?, ValueTextChannel)` / 返回不是 `string`）由源生成器在编译期报 `AXA015`，消息里直接给出正确签名；
  - 值形态与通道不匹配（格式串用在 `bool` / 二进制形态上、`[Format]` 标在结构化 `[Body]` 或文件部件上）在构建绑定计划时抛 `InvalidOperationException`。
- 没启用源生成器时 `AXA015` 不生效，改用 `ApiClient.ValidateModels(...)` / `ValidateInterfaces(...)` 在启动期扫出来。
- **Native AOT**：源生成器把格式化委托直接烘焙进元数据（签名不合法报 `AXA015`），运行期不需要反射。手动 `RegisterShape<T>()` 的形状没有烘焙，AOT 下用 `[Format(typeof(...), nameof(...))]` 会显式报错并指向「启用源生成器或改用格式串」，不会静默丢格式化。

### 从 OpenAPI 导入

已有接口文档时，不必手写上面这些声明。`src/AxiosNet.Cli` 是设计期 dotnet 工具（命令 `axiosnet-gen`，包 `AxiosNet.Cli`），读取 OpenAPI 3.0.x / 3.1.x、Swagger 2.0 文档（JSON / YAML；本地文件、http(s) URL 或 `-` 标准输入），生成声明式 C#。

| 形态 | 产物 | 调用写法 |
| --- | --- | --- |
| 模型式（默认） | 每个操作一个 `ApiRequest<T>` 请求模型 | `client.RequestAsync(new XxxRequest { ... })` |
| 接口式（`--style interface`） | 每个 tag 一个接口 | `client.Create<T>()` |

两种形态都只有声明、不含运行时代码：模型与接口代理都由源生成器在编译期烘焙，JIT 与 Native AOT 同一路径。工具自身的 YAML 解析是设计期依赖，`AxiosNet` 的 `net6.0` / `net8.0` 运行时仍零第三方依赖。

```bash
# 一次性安装（全局 dotnet tool，命令 axiosnet-gen；net6.0 / net8.0 双目标，.NET 6 及以上都能跑）
dotnet tool install -g AxiosNet.Cli

# 生成（默认模型式）
axiosnet-gen demo.openapi.json -o Generated -n MyApp.Api

# 接口式
axiosnet-gen demo.openapi.json -o Generated -n MyApp.Api --style interface

# 远端文档 + 导入报告
axiosnet-gen https://api.example.com/openapi.json --namespace MyApp.Api --output Generated --report import.json

# 改工具本身时，仓库内直接跑源码
dotnet run --project src/AxiosNet.Cli -f net8.0 -- demo.openapi.json -o Generated -n MyApp.Api
```

模型式的产物长这样（`users` tag → `UsersRequests.g.cs`，每个操作一个类；属性名按 PascalCase 生成，wire 名不同时显式给出）：

```csharp
[Get("/api/users")]
[Authorize]                                             // 根级 / 操作级 security
public sealed class ListUsersRequest : ApiRequest<User[]>
{
    [Query("role")] public string? Role { get; set; }
    [Query("ids")]  public long[]? Ids { get; set; }

    protected override void Configure(AxiosConfig config)
    {
        config.Serialization.CollectionFormat = AxiosCollectionFormat.Multi;   // explode: true
    }
}
```

`Configure` 是模型式表达方法级编码的出口（特性没有可用的方法级位置）：

- 非 JSON 请求体在这里写 `config.ContentType`（表单 / XML / 自定义媒体类型）；
- 查询数组写 `config.Serialization.CollectionFormat`。

流式端点额外生成 `public const AxiosStreamFormat StreamFormat`，用 `client.RequestStreamAsync(new StreamEventsRequest(), StreamEventsRequest.StreamFormat)` 逐帧读。响应无正文时生成 `ApiRequest<object>`，照常 `RequestAsync`，`Data` 为 `null`。

**默认映射规则**（都有开关或报告提示）：

- **分组**：按操作的第一个 tag 分组。
  - 模型式每个分组一个文件（`users` → `UsersRequests.g.cs`），请求类名取 `operationId` + `Request`（重名时自动加序号，且不会遮蔽 `ApiRequest` 等运行时类型）。
  - 接口式每个 tag 一个接口（`users` → `IUsersApi`；tag 已是 `XxxApi` 时加 `I` 前缀）。
  - 无 tag 的操作进 `DefaultRequests.g.cs` / `IDefaultApi`。
  - `--single-file` 全部写进一个文件；`--single-interface`（配合 `--interface-name`，默认 `IApiClient`）只在接口式下可用。
- **参数与正文**：`path` / `query` / `header` 对应 `[Path]` / `[Query]` / `[Header]`（模型式落在属性上，接口式落在参数上）。
  - 请求体按媒体类型选一份，优先级 JSON > `+json` > `*/*` > 表单 > multipart > 文本 > 二进制 > 第一份。
  - JSON 生成强类型 `[Body]`；`application/x-www-form-urlencoded` 模型式写进 `Configure` 的 `config.ContentType`、接口式生成 `[BodyContentType(...)]`；`multipart/form-data` 的对象属性展开成 `[Part("wire")]`（`format: binary` → `byte[]`）；文本正文 → `string`；二进制 → `byte[]`。
  - 多份候选记 `MULTIPLE_REQUEST_CONTENT`。
- **查询数组**：`style: form` + `explode: true` → `Multi`（`ids=1&ids=2`）；`explode: false` → `Csv`；`spaceDelimited` / `pipeDelimited` → `Ssv` / `Pipes`；Swagger 2 的 `collectionFormat` 同样映射。模型式写进 `Configure`，接口式生成 `[QueryCollectionFormat(...)]`。
- **响应**：在 2xx 中按状态码升序取第一个（其次 `2XX`、`default`）。
  - 模型式生成 `ApiRequest<T>`，调用得到 `AxiosResponse<T>`；无正文时 `ApiRequest<object>`。
  - 接口式生成 `Task<AxiosResponse<T>>` / `Task<AxiosResponse>`，`--response-wrapper none` 改为只交付 `Task<T>`（仅接口式）。
  - `text/event-stream` 或文档里标注 `x-axios-stream` 时：模型式生成 `StreamFormat` 常量与帧类型，接口式生成 `[Stream(...)]` 与 `Task<AxiosResponse<IAsyncEnumerable<T>>>`；数组 schema 按元素类型逐帧。
  - 多状态码 / 多份内容分别记 `MULTIPLE_SUCCESS_RESPONSES` / `MULTIPLE_RESPONSE_CONTENT`。
- **授权**：根级或操作级 `security` → `[Authorize]`，但只覆盖 http bearer / basic。
  - `apiKey` / `oauth2` / `openIdConnect` 与其他 http scheme 记警告、不生成授权声明，由调用方在 `AxiosClient` 的 Authorization 或请求配置里自行设置（apiKey 按文档要求补对应的头 / 查询参数 / Cookie）。
  - `security: []` → `[AllowAnonymous]`。
  - 模型式落在请求类上；接口式在同组所有操作要求同一 scheme 时才提到接口上，否则落在方法上。
- **模型**：
  - `allOf` 摊平为字段。
  - 3.0 `nullable`、3.1 `type: [..., "null"]` 与 Swagger `x-nullable` 都映射为可空。
  - `oneOf` / `anyOf` 无法用声明式类型表达，主类型按 `JsonElement` 生成并同时生成各成员类型，由调用方自行判别。
  - 仅当全部取值都是合法 C# 标识符时生成 `enum`（成员名即线上值），否则生成常量类 + `string`；数值枚举保持数值类型。
  - 生成的每个属性都带显式 `[JsonPropertyName]`，命名与 `System.Text.Json` 默认选项一致。
- **缺口不猜**：断链 `$ref`、外部引用、cookie 参数、非标量（数组 / 对象 / `label`·`matrix` 风格）的 path / header 参数、缺 `items` 的数组、循环引用等都会给出稳定警告码并降级（`JsonElement` / 跳过整个操作 / `byte[]`），不会静默丢字段。`--fail-on warnings` 可把它们变成退出码 1，完整清单在 `--report` 的 `warnings` 里。

**落地安全**：

- 输出确定性（LF 换行、排序稳定），每个文件带 `// <auto-generated />`；同目录的 `axiosnet-gen.manifest.json` 记录上次生成的文件列表，失联的旧文件也留在清单里。
- 产物用文件范围内的命名空间与 `#nullable enable`，**消费方需 C# 10 及以上**（`net472` 工程默认 7.3，要显式设 `<LangVersion>`）。
- `--check` 只比较不写文件：缺文件、内容过期或存在失联旧文件都返回 1，适合放进 CI 防手改。
- 不带生成标记的文件默认拒绝覆盖，`--force` 才放行；删除失联旧文件也只在 `--force` 下发生，且只删带生成标记的文件——用户自己写的文件永远不会被自动删除。
- `--dry-run` 把内容打到 stdout；远端文档用 `--header "Authorization: Bearer ..."` 与 `--timeout <seconds>`。

完整走通这一流程的是示例 [`OpenApiGeneratedExample`](samples/AxiosNet.Simple/WebAPI/Examples/OpenApiGeneratedExample.cs)：它把 [`samples/AxiosNet.Simple/WebAPI/demo.openapi.json`](samples/AxiosNet.Simple/WebAPI/demo.openapi.json) 以默认模型式生成到 [`WebAPI/Generated/`](samples/AxiosNet.Simple/WebAPI/Generated)，再用 `RequestAsync` / `RequestStreamAsync` 调用 `DemoApiServer` 的真实路由。

生成的覆盖面包括 tag 分组的 `*Requests.g.cs`、根级授权、表单登录、multipart、SSE 与 JSON 数组流式。仓库内重新生成这批产物：

```bash
dotnet run --project src/AxiosNet.Cli -f net8.0 -- \
  samples/AxiosNet.Simple/WebAPI/demo.openapi.json \
  -o samples/AxiosNet.Simple/WebAPI/Generated \
  -n AxiosNet.Simple.WebAPI.Generated --force
```

### 凭据与令牌管理

声明式客户端只有一个授权扩展点：`ApiClient.CredentialProvider`（`IApiCredentialProvider`）。每次请求发送前调用一次，返回的 `ApiCredential` 决定库怎么落头；`[AllowAnonymous]` 跳过整条链路。它最终只是内核唯一凭据槽位 `Transport.Auth` 的适配器——直接使用内核时也可以给这个槽位配静态凭据：

```csharp
// 直接配置内核凭据槽位（四选一）
config.Transport.Auth = AxiosCredential.Basic("user", "pass");      // 静态 Basic
config.Transport.Auth = AxiosCredential.Bearer("token");           // 静态 Bearer（可传第二个参数改 scheme）
config.Transport.Auth = AxiosCredential.Header("Signature abc");   // 完整头值
config.Transport.Auth = AxiosCredential.Provider(source);          // 每请求调用一次的提供方

// 声明式调用方保持原写法（内部包成上面的提供方形态）
var client = new ApiClient("https://api.example.com/")
{
    CredentialProvider = CredentialProviders.Fixed("token")
};
```

| 返回值 | 落点 | 401 重放 |
| --- | --- | --- |
| `ApiCredential.Token(token)` | 按 `[Authorize]` 的 scheme（默认 `Bearer`）写 `Authorization: {scheme} {token}` | 支持 |
| `ApiCredential.Header(value)` | `value` 作为完整 `Authorization` 头值原样发送，不做 scheme 拼接（等价 `Header("Authorization", value)`） | 支持 |
| `ApiCredential.Header(name, value)` | 请求头 `{name}: {value}`（覆盖同名头），如 `X-Api-Key`；`name` 是 `Authorization`（忽略大小写）时按完整头值处理 | 支持 |
| `ApiCredential.Query(name, value)` | 查询参数 `?{name}={value}`（覆盖同名参数），如 `access_token` | 支持 |
| `ApiCredential.Custom()` | 提供方已自行写配置（正文令牌、一次写多个头、正文签名），库不碰 | 不重放 |
| `ApiCredential.None`（或 `Token(null)`） | 本次不带凭据 | 不重放 |

- **一个槽位，一个来源**：`Authorization` 头落点只有 `Transport.Auth` 一个槽位，形态四选一（静态 Basic / 静态令牌 / 完整头值 / 提供方）。`CredentialProvider` 是提供方形态的适配器，`Transport.BasicAuth` 已并入本槽位。`Header`（非 `Authorization` 头名）/ `Query` 落点不占该槽位，直接写进本次配置。
- **冲突报错，不静默压制**：显式 `Authorization` 头与凭据槽位同时设置、或 `CredentialProvider` 与已设置的 `Transport.Auth` 同时存在，都在发出前抛 `ERR_BAD_OPTION_VALUE`；只设其一时按该来源落头。「凭据 → 头值」映射只有内核一份实现，WebAPI 只提供值与 401 重放策略。`Header`（非 `Authorization` 头名）/ `Query` 落点按覆盖语义写同名键，不与显式头做冲突判定。
- **401 重放认托管落点**：`Token` / `Header` / `Query` 三种由库写入的形态都会在 401 后作废旧值、重取一次、重放一次；`Custom` / `None` 不重放。静态 Basic / 静态令牌 / 完整头值不参与重放，需要刷新请改用 `CredentialProvider`（内核通过 `IsRefreshable` 暴露该来源是否可刷新）。
- **声明里的 scheme 与静态槽位必须一致**：`[Authorize("Token")]` 这类带 scheme 的声明，与已设置的 `Transport.Auth` 静态凭据 scheme 不一致时，发出前抛 `ERR_BAD_OPTION_VALUE`，不静默以槽位为准。比如静态配了 Basic，声明就要写 `[Authorize("Basic")]`；令牌配成 `AxiosCredential.Bearer(token, "Token")` 才能匹配 `[Authorize("Token")]`。
- **不白取令牌**：显式头与提供方并存时先报冲突，提供方一次都不会被调用。
- **`Token` 永远拼 scheme**：令牌形态只负责「令牌本体」，要发 `Basic xxx` / `Signature xxx` 这类完整头值请用 `ApiCredential.Header(value)`。
- **`Header` / `Query` 是可托管落点**：API Key 类接口用 `ApiCredential.Header("X-Api-Key", key)`，查询参数类（企业微信的 `access_token`）用 `ApiCredential.Query("access_token", token)`。两者都支持按请求切换落点名（`ctx.Request` 分支），也都进缓存身份键——不同 key 不会共用同一条响应缓存。落点名不能是空白（`ArgumentException`），值空白等同 `None`。
- **`Header` 一个名字一条规则**：省略头名时写 `Authorization` 完整值（不拼 scheme），给了头名就写那个头；头名是 `Authorization`（忽略大小写）时两者行为一致——都走内核凭据槽位，与显式 `Authorization` 头并存时报冲突。
- **自定义凭据**：值依赖请求本身时（令牌放正文、一次写多个头、签业务对象而非原始字节）返回 `ApiCredential.Custom()`，自己写 `ctx.Config.Headers` / `ctx.Config.Params`（此时配置已合并模型绑定结果）；按请求分支用 `ctx.Request`（接口式调用是 `ApiInterfaceInvocation`）。`Custom` 不由库托管，因此不重放——需要 401 后强制刷新，给 `Create` 传 `invalidate:` 委托自行处理。签**最终正文 / 最终 URL** 也可以走这里，注意 `transformRequest` 与 401 重放的取舍，见 [托管令牌 + 正文签名](#托管令牌--正文签名)。
- **手写查询参数用 `SetQuery`**：`ctx.Config.SetQuery(name, value)` 在 `Params` 为空时新建字典、已是键 `string` / 值 `object` 的可变字典时原地写入；其它形态（对象 / 查询串 / 只读字典）抛 `ERR_BAD_OPTION_VALUE`，不静默替换掉原有参数。托管落点请直接用 `ApiCredential.Query`，不必自己写。
- **`Fixed` 有两个入口**：`Fixed(ApiCredential)` 接受任意落点；`Fixed(string)` 是 `Fixed(ApiCredential.Token(...))` 的短名，额外把空白令牌判为配置错误（`Fixed(" ")` 抛 `ArgumentException`，要表达「本次不带」用 `Fixed(ApiCredential.None)`）。
- **401 作废**：`IApiCredentialProvider.Invalidate(staleToken)` 由库在 401 后调用，默认空实现；入参是上次落点值（`Header` 形态拿不到令牌本体，传 `null` 无条件作废），带值可避免并发下把别人刚刷新的新令牌也作废。
- 容器里注册 `IApiCredentialProvider` 即可自动挂到每个 `ApiClient`（见[依赖注入](#依赖注入)）。

#### 内置令牌管理器

`AccessTokenManager` 实现了同一个接口，负责**何时刷新、并发刷新只发一次**，而「怎么换令牌」的 HTTP 调用仍由你给（库不替你调 token 端点）。

```csharp
using var tokens = new AccessTokenManager(new AccessTokenOptions(AcquireAsync)
{
    RefreshAsync = RefreshAsync,                        // 省略时回落到 AcquireAsync
    RefreshAhead = TimeSpan.FromSeconds(30),            // 默认 1 分钟
    RefreshTimeout = TimeSpan.FromSeconds(10)           // 默认 30 秒
});

using var client = new ApiClient("https://api.example.com/") { CredentialProvider = tokens };

// 两个回调都返回令牌与过期时间（ExpiresAt 为 null 表示不过期）
static ValueTask<AccessToken> AcquireAsync(CancellationToken ct) { ... }
static ValueTask<AccessToken> RefreshAsync(CancellationToken ct) { ... }
```

- **缓存命中零等待**：令牌未进入刷新窗口时直接返回，不碰回调。
- **并发刷新单飞**：同一时刻只有一个刷新在途，其余请求等同一个结果；刷新失败不缓存，下一次取令牌会重试。
- **刷新不跟随调用方取消**：一个请求断开不会让同批等待者一起失败，也不会白刷一次（刷新自身受 `RefreshTimeout` 约束，超时抛 `TimeoutException`）。
- **提前量自动收敛**：实际提前量 = min(`RefreshAhead`, 令牌寿命的一半)，短寿命令牌不会「刚拿到就该刷」。
- **401 后强制刷新**：`tokens.Invalidate(staleToken)` 作废当前令牌（带旧令牌值可避免并发下把别人刚刷新的新令牌也作废），下一次取令牌必然刷新。
- **取令牌不需要请求上下文**：`tokens.GetTokenAsync(ct)` 直接返回令牌文本，用于自己决定落点的提供方（查询参数 / 自定义头）；`GetTokenAsync(context, ct)` 等价，`context` 不参与取令牌。
- **401 自动重放**：收到 401 且本次 `Authorization` 头由库托管写入时，作废凭据、重新取一次并重放**一次**（第二次仍 401 就按错误模型抛出；新凭据与旧值相同则不重放）。重放沿用同一个幂等键，非幂等方法需显式打开 `Resilience.AllowUnsafeRetry`（与内核重试同一开关）。用 `ApiClient.RetryOnUnauthorized = false` 关掉。
- **401 只归一层**：凭据槽位是提供方形态时，内核不再重试 401（即使把 401 写进 `RetryStatusCodes`），401 的刷新与重放只由声明式层负责，避免两层循环。
- **重放时的正文**：数据型正文（`null` / `byte[]` / `string` / 业务对象 / 无流部件的 `FormData`）由请求模型重新装配；白名单内存内容（`ByteArrayContent` / `StringContent` / `FormUrlEncodedContent` / `ReadOnlyMemoryContent` / `JsonContent` / 全部部件可重放的 `MultipartContent`）在首次发送前做正文快照，受 `Limits.MaxRequestBytes` 约束——超限则不建快照、本次不重放（不会静默发第二次空正文）。`Stream`、含流部件的 multipart、`StreamContent` 与自定义内容一律不重放；声明里显式写了 `Authorization`、`[AllowAnonymous]` 也不重放。

#### 用同一个客户端自举取令牌

取令牌通常就是本客户端的另一个接口。给登录 / token 请求标 `[AllowAnonymous]`，它会跳过凭据链路，不会递归回到 `AccessTokenManager`；因此取令牌与刷新回调可以直接调同一个 `ApiClient`：

```csharp
[Post("login")]
[AllowAnonymous]
public sealed class LoginRequest : IApiRequest<TokenResponse>
{
    [Body] public LoginPayload Credentials { get; set; } = null!;
}

ApiClient? api = null;
using var tokens = new AccessTokenManager(new AccessTokenOptions(async ct =>
{
    var r = await api!.RequestAsync(new LoginRequest
    {
        Credentials = new LoginPayload("alice", "wonderland")
    }, cancellationToken: ct);
    return new AccessToken(r.Data!.Token, DateTimeOffset.UtcNow.AddSeconds(r.Data.ExpiresIn));
}));

api = new ApiClient("https://api.example.com/") { CredentialProvider = tokens };
using var client = api;
```

`/login` 请求不带 `Authorization` 头；401 刷新重放同样走这个匿名接口。回归测试见 `test/AxiosNet.Tests/WebAPI/SelfHostedTokenEndpointTests.cs`。

#### 凭据落点：任意请求头 / 查询参数

不是所有服务都把令牌放在 `Authorization` 头：API Key 类接口用自定义头（`X-Api-Key`），企业微信这类用查询参数（`access_token`）。这两种落点同样是托管凭据——库写值、401 后刷新重放，`AccessTokenManager` 的缓存与提前刷新照常生效：

```csharp
// 任意请求头
client.CredentialProvider = CredentialProviders.Fixed(ApiCredential.Header("X-Api-Key", apiKey));

// 查询参数（配令牌管理器，401 后自动换新值重放）
using var tokens = new AccessTokenManager(new AccessTokenOptions(AcquireAsync));
client.CredentialProvider = CredentialProviders.Create(async (_, ct) =>
    ApiCredential.Query("access_token", await tokens.GetTokenAsync(ct)));
```

落点名可以按请求切换（`ctx.Request` 分支）；同一落点重复写是覆盖语义。值依赖请求本身的场景（令牌放正文、一次写多个头、正文签名）都可以用 `ApiCredential.Custom()`，取舍见 [托管令牌 + 正文签名](#托管令牌--正文签名)。

`Header` / `Query` 落点的服务常用 200 + 业务错误码表达令牌失效（不走 401），这类情况在错误处理里调 `tokens.Invalidate()`，下一次请求就会重新取令牌。

#### 托管令牌 + 正文签名

既要托管令牌（401 自动换新重放），又要对最终正文签名，有两种写法：

| 写法 | 401 自动重放 | 适用 |
| --- | --- | --- |
| 令牌走托管落点 + 签名走 `OnSendingRequest` | 有 | 通用；钩子看到的是最终字节与最终 URL |
| `ApiCredential.Custom()` 里一次做完 | 无 | 没有 `transformRequest`，且不需要 401 自动重放 |

前者把凭据与签名各归各的层：令牌照常刷新重放，钩子跑在正文序列化、凭据头写入之后，拿到的就是最终字节。

```csharp
using var tokens = new AccessTokenManager(new AccessTokenOptions(AcquireAsync));

using var api = new ApiClient("https://api.example.com/", defaults =>
{
    defaults.Observability.OnSendingRequest = async (request, config, ct) =>
    {
        // 此刻正文已序列化、URL 已拼好、X-Api-Token 头已写入
        var body = request.Content is null
            ? Array.Empty<byte>()
            : await request.Content.ReadAsByteArrayAsync(ct);

        var token = request.Headers.TryGetValues("X-Api-Token", out var values) ? values.First() : string.Empty;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var nonce = Guid.NewGuid().ToString("N");

        request.Headers.TryAddWithoutValidation("X-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-Nonce", nonce);
        request.Headers.TryAddWithoutValidation(
            "X-Signature",
            Sign(request.Method.Method, request.RequestUri!.PathAndQuery, timestamp, nonce, token, body)); // Sign 为你的签名函数
    };
});

api.CredentialProvider = CredentialProviders.Create(async (_, ct) =>
    ApiCredential.Header("X-Api-Token", await tokens.GetTokenAsync(ct)));
```

- **钩子能看到什么**：最终绝对 URI（路径参数已替换、query 已合并转义）、已序列化的正文字节、**已写入的凭据头**；每次尝试拿到的是独占副本，改头不会污染重试 / 对冲的其它分支。
- **401 重放按新令牌重签**：重放重新走「解析凭据 → 拼 URL → 触发钩子」，签名里的令牌与头里的令牌天然一致。
- **签名头进不了缓存键**（建键早于钩子）。开缓存时令牌仍会进键，但缓存命中不发请求、也就不会签名——签名接口建议关缓存，或只对 `Cache-Control: no-store` 的响应落库。
- **每次尝试都触发**：重试 / 换端点 / 对冲各签一次，时间戳与 nonce 都是新的，这正是签名要的；幂等键不要写在这里（每次换值等于两次写操作），用 [`Resilience.Idempotency`](#幂等键)。
- **读正文有代价**：已缓冲正文（JSON / 表单 / `byte[]` / `string`）直接取字节；流式正文会被读走。只签已缓冲正文，或只签头。
- **异常归类**：钩子抛异常 → `ERR_BAD_REQUEST`，请求不发出、不占连接；钩子里的取消 / 超时按本次尝试归属（`ERR_CANCELED` / `ECONNABORTED`）。

**`Custom()` 也能一次做完**：`ctx.Config.Url` 里的路径参数已经替换，`ctx.Config.Body` 是业务对象，把序列化结果写回 `Body` 库就会原样发送，于是「签的」与「发的」是同一份。序列化选项要与库内一致（`Serialization.JsonOptions` 优先，其次 `AxiosJson.Options`），否则线上格式会和平时不同。

```csharp
api.CredentialProvider = CredentialProviders.Create(async (ctx, ct) =>
{
    var token = await tokens.GetTokenAsync(ct);

    // 正文定成最终字节并写回 Body：库原样发送，不再二次序列化
    var body = ctx.Config.Body switch
    {
        null => Array.Empty<byte>(),
        byte[] raw => raw,
        string text => Encoding.UTF8.GetBytes(text),
        var value => JsonSerializer.SerializeToUtf8Bytes(value, ctx.Config.Serialization.JsonOptions ?? AxiosJson.Options),
    };
    if (ctx.Config.Body is not (null or byte[] or string))
    {
        ctx.Config.Body = body;
    }

    // 路径参数已替换；这里补齐 BaseAddress 与 params，不发请求
    var uri = api.Axios.GetUri(ctx.Config);

    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
    ctx.Config.Headers.Set("X-Api-Token", token);
    ctx.Config.Headers.Set("X-Timestamp", timestamp);
    ctx.Config.Headers.Set("X-Signature", Sign(ctx.Config.Method.Method, uri, timestamp, token, body)); // Sign 为你的签名函数

    return ApiCredential.Custom();
});
```

代价是这三件事要自己兜：

- **`transformRequest` 会在你之后改正文**：transform 链作用于 `config.Body`，签的是加工前、发出去的是加工后。项目里注册过 transformRequest（统一拆包 / 加壳）时不要走这条路，用上面的钩子写法。
- **401 不重放**：`Custom` 由库判为不可刷新，`invalidate:` 只影响下一次取令牌，这一次 401 照样抛；要重放请在调用方 catch 后重发。
- **身份不进缓存键**：`Custom` 不写落点身份，换令牌仍会命中同一条缓存，需要自己配 `Caching.KeySelector`。

签名对象是业务对象而非原始字节、或要一次写多个互相派生的头时，`Custom` 反而是最直接的写法。

## 缓存

两者都让请求「不发出也能拿到响应」，区别在谁决定：短路由调用方直接给一份响应，缓存由库按键查。先讲手动的短路，再讲内置缓存。

### 响应缓存

`c.Caching` 是内置的响应缓存：命中时**不发请求**，直接把上次的响应交付（走与[短路响应](#短路响应)同一条路径）。它同时负责**单飞**——同一个键的并发请求只发一次，其余等这一次的结果。

```csharp
var cache = new AxiosCache(new AxiosCacheOptions
{
    TimeToLive = TimeSpan.FromSeconds(30),   // 默认 30 秒
    SingleFlight = true,                     // 默认 true
    MaxEntries = 1024,                       // 默认 1024
    MaxBytes = 64L * 1024 * 1024,            // 默认 64 MB，0 = 不限制
    StoreTimeout = TimeSpan.FromSeconds(2),  // 默认 2 秒，null = 不限时（只约束自建存储）
    FreshnessMode = CacheFreshnessMode.Configured  // 默认：只信 TimeToLive；另有 Server / Intersection
});

client.Defaults.Caching = cache;               // 挂在实例默认值上，整个实例生效

var first = await client.GetAsync<User>("users/1");   // 发请求并落库
var again = await client.GetAsync<User>("users/1");   // 命中：没有发出去
```

| 项 | 默认 | 说明 |
| --- | --- | --- |
| `TimeToLive` | 30 秒 | 条目存活时长，必须大于零 |
| `FreshnessMode` | `Configured` | 缓存时长怎么来：`Configured` 只信配置 TTL；`Server` 只信服务器；`Intersection` 取交集（见下） |
| `SingleFlight` | `true` | 同键并发只发一次（非 2xx 也共享响应，见下） |
| `MaxSharedFailureBodyBytes` | 256 KB | 非 2xx 单飞共享正文的字节上限；超限或设为 `0` 时跟随者各自补发（流式响应永不共享） |
| `ConditionalRequests` | `true` | 过期条目带 `ETag` / `Last-Modified` 时发条件请求，304 直接复用缓存的正文（见下） |
| `Store` | `null` | 条目存储，`null` = 内置 `MemoryCacheStore`；接 Redis / 共享缓存时换成自己的 `IAxiosCacheStore` |
| `MaxEntries` | 1024 | 内置内存存储的条目上限：满了先清过期条目，仍满就不再收新条目（自建 `Store` 时本项不生效） |
| `MaxBytes` | 64 MB | 内置内存存储的近似字节上限（正文 + 头长度估算，`0` = 不限制）：和条目满同一口径，放不下就不收新条目（自建 `Store` 时本项不生效） |
| `StoreTimeout` | 2 秒 | 单次落库超时：存储挂起时按「这次没缓存」处理，不把响应一起拖住（`null` / 非正数 = 不限时；只约束自建存储；实际等待还被剩余 `Resilience.TotalTimeout` 封顶） |
| `KeySelector` | `null` | 自定义键（参数是生效配置与最终 URL），默认 `AxiosCache.CreateKey` |
| `ShouldCache` | 默认口径 | 哪些请求参与缓存，默认为 `AxiosCache.IsCacheable`（GET / HEAD、无短路响应、非流式、无请求体、非范围请求），可在此基础上组合 |

**键 = 方法 + 绝对 URL（+ 凭据摘要）**：凭据（显式 `Authorization` 头或 `Transport.Auth` 解析出的头值）参与键，且只以 64 位摘要进键——同一实例上不同用户的同名接口不会串用同一条缓存。用 Cookie 或自定义头区分身份、或要按租户 / 语言分区时，给 `KeySelector`。

**两条边界**：`?token=` 这类查询串令牌虽然因为 URL 不同不会串用，但令牌会**明文**进缓存键（还会进审计事件的 URI），而 `Authorization` 只以摘要进键——敏感令牌不要放查询串，或由 `KeySelector` 只取摘要；`OnSendingRequest` 里写的身份头**不参与键**（键早于钩子计算），要按身份分区请挪到按请求配置或请求拦截器里写。

**存的是原始正文字节**，命中时按本次调用的 `ResponseType` 与泛型参数重新解析：同一份字节喂给 `GetAsync<User>` 与 `GetAsync<string>` 各得该有的形态。

**条目尽量与响应共用正文**：JSON / 文本形态的 `Data` 是另建的对象，调用方拿不到这块数组，条目就直接与响应共用同一块内存，不再克隆。否则调用方一次改写就污染后续命中。

以下两种情况条目自持一份：`byte[]` / `ArrayBuffer` 形态的 `Data` 就是原始字节本身，或配了 `transformResponse`（它可能把这块数组包进自己的对象，库无法证明调用方没拿到引用）。命中缓存时 `byte[]` / `Stream` 形态同样每次交付独立副本（条目不能被某次写入污染）。

只有同时满足「状态码 2xx」「拿到了原始字节（非流式）」「`ShouldCache` 通过」才落库；空正文同样算原始字节（HEAD / 204 本就没有正文，缓存的是头）。流式响应、响应拦截器换了响应对象、写缓存失败都只是「这次不入缓存」，不影响请求本身。范围请求（带 `Range` 头）默认不参与缓存，206 / 带 `Content-Range` 的响应也不落库——键只有方法 + URL，分段正文会被后续普通 GET 当成整份内容交付。

**流式请求不参与缓存**：`ResponseType = Stream` 的请求默认连查询都不做（调用方要的是流，命中会把整份正文缓冲进内存）；已落库的条目仍可被普通调用命中。要让流式请求也查缓存，用 `ShouldCache` 放行 —— 但请确认这正是你要的所有权语义。

**三种取值方式**（`FreshnessMode`）：

| 模式 | 落库判定 | 生效 TTL |
| --- | --- | --- |
| `Configured`（默认） | 只走客户端闸门，忽略请求与响应的 HTTP 指令；**落库前不解析响应头**（存储读策略的 HTTP 字段时才按需解析） | `TimeToLive` |
| `Server` | `no-store` / `no-cache` / `Vary: *` / 已过期或非法的 `Expires` 不落库；**没有声明任何新鲜期也不落库** | 服务器剩余新鲜期（`min` 上请求的 `max-age`），不受 `TimeToLive` 上限约束 |
| `Intersection` | 同上（服务器与请求侧否决都优先） | `min(TimeToLive, 剩余新鲜期, 请求的 max-age)`；无新鲜期时用 `TimeToLive` |

剩余新鲜期 = `max-age`（没有则 `Expires - Date`）减去龄期（`Age` 头与「收到时刻 − `Date`」的较大值）。`max-age` 出现但值非法或为负时按零（RFC 9111 §4.2.1：无效的新鲜期信息按已过期处理），此时 `Expires` 不再兜底。两种解析模式都不做 `Last-Modified` 启发式估算。

`Server` / `Intersection` 同时解析**请求**侧的 `Cache-Control` / `Pragma`，与响应侧取交集：

| 请求指令 | 查缓存 | 落库 |
| --- | --- | --- |
| `no-store` | 跳过 | 不落库 |
| `no-cache`（或旧式 `Pragma: no-cache`） | 跳过新鲜查询，但拿旧条目发条件请求（304 直接复用正文） | 仍可落库 |
| `max-age=0`（含非法或负数值，按零处理） | 跳过 | 不落库 |
| `max-age=N`（正数） | 照常 | 落库 TTL 不超过 N |

`Configured` 是「强制按配置执行」：请求与响应的 HTTP 指令都不参与取舍。

**条件请求（`ETag` / 304 复用）**默认开启（`ConditionalRequests = true`）：新鲜条目命中照旧不发请求；只有「新鲜条目没有、但存储里还留着一条带校验器的旧条目」时，把校验器写成条件头再发一次——

- **`ETag` 优先**，写成 `If-None-Match`；没有 `ETag` 才用 `Last-Modified` 写 `If-Modified-Since`；两者都没有就照常完整请求。
- **304 的交付语义**：状态码与正文取缓存里的那一份，响应头按 304 更新（`Content-Length` / `Transfer-Encoding` / `Content-Range` 不覆盖——304 没有正文；`Set-Cookie` 这类可重复的头按多条原样交付，不折叠），随后按新的响应头**重新落库续上 TTL**（这次请求本身允许落库时），下一次调用直接命中。`response.Request` 不为 `null`（与「命中没发请求」区分）。
- **调用方自己写了条件头时以调用方为准**：库既不覆盖它，也不接管由此得到的 304（那仍是一次普通的非 2xx 响应，按 `ValidateStatus` 处理）。
- **存储要配合**：`MemoryCacheStore` 会保留带校验器的过期条目（不带校验器的过期条目照旧在读到时摘除并退还字节配额）。自建存储实现 `IAxiosCacheRevalidationStore.GetRevalidationAsync` 即可参与；只实现基础 `IAxiosCacheStore` 的存储拿不出旧条目，退化成每次完整下载，请求本身不受影响。
- **请求指令照常生效**：`no-store` 不读不写；`no-cache` / `max-age=0` 不发新鲜查询，但会拿旧条目做条件请求（RFC 9111 §5.2.1.4），304 时仍交付缓存的正文。
- **遥测**：304 复用记 `cache.revalidations`（无标签）并在 span 上打 `axiosnet.cache.revalidated=true`；它按未命中计入命中率（请求确实发出去了），单独看这条才知道省了多少带宽。

库按**私有缓存**解释：`private` / `public` 不改变落库判定，`s-maxage` 不采用，`Vary` 不参与键（分区仍用 `KeySelector`；`Vary: *` 在解析模式下不落库）；**内置 `MemoryCacheStore` 不按 `Vary` 分区，命中时也不校验 `Vary`**——同一 URL 的不同变体会共用同一条条目，需要该语义请用 `KeySelector`，或在自建存储里按 `AxiosCacheEntryPolicy.Vary` 自行分区；不支持 `stale-while-revalidate` 之类的 `stale-*` 服务。这些信息不会丢——它们都在 `AxiosCacheEntryPolicy` 里交给存储。

| | 短路响应 | 响应缓存 |
| --- | --- | --- |
| 谁决定「不发」 | 调用方直接给一份响应 | 库按键查缓存 |
| 存活范围 | 只属于那一次请求 | 实例级共享，跨请求复用 |
| 命中时 | 反序列化 / `transformResponse` / `ValidateStatus` / 响应拦截器照常 | 同上 |
| 并发闸门 / 遥测 span | 不参与 | 不参与 |

**命中与落库**

- **命中不发请求**：不占并发闸门、不产生遥测 span 与在途计数，`response.Request` 为 `null`（与短路响应同一处可辨痕迹）。
- **响应头按全部值存**：同名多值头（如多个 `WWW-Authenticate` challenge）原样回放；`Headers` 单值视图保留首值供第三方存储按契约读取。`Set-Cookie` / `Set-Cookie2` 不进缓存——会话态只属于那一次响应，命中回放等于把它塞给下一次请求。
- **键不含请求体**：默认口径只缓存无正文的 GET / HEAD；用 `ShouldCache` 放行带正文的方法时，必须在 `KeySelector` 里把正文特征算进键，否则不同正文会串用同一条缓存。
- **策略挂在实例上**：一个 `AxiosCache` 就是一份策略；要按接口区分存活时长或键规则，建多个实例分别挂。

**单飞与预算**

- **HTTP 状态类失败按响应共享**：领导者拿到非 2xx 时，单飞把状态码 / 响应头 / 原始正文交给所有跟随者；每个跟随者用自己的 `ValidateStatus`、响应拦截器与错误工厂（WebAPI `ErrorFactory`）判定，`Response` 与 `Config` 都属于各自调用方，服务端全程只发一次。
- **共享的边界**：`ResponseType.Stream`（只能读一次）永不共享；正文超过 `MaxSharedFailureBodyBytes`（默认 256 KB）或把该项设为 `0` 时退回各自补发；瞬时内存约等于「错误正文 × 在途跟随者数」。共享结果只走本次单飞，不写入 `IAxiosCacheStore`（HTTP 缓存契约不存非 2xx），存储实现者无需扩展。
- **传输 / 取消类失败仍广播**：领导者抛出非状态类错误时，等待者拿到同一个 `AxiosException`；但领导者被**它自己的调用方**取消时不算失败——等待者各自补发一次。
- **等待者可以被取消**：`CancellationToken` 取消只放弃等待，不会取消那次共享的发送（`ERR_CANCELED`）。
- **等待受总预算约束**：跟随者的等待不超过剩余 `Resilience.TotalTimeout`，到点按 `ECONNABORTED`（`IsTotalTimeoutExceeded`）抛出，不会挂在原地等一个慢领导者。
- **查询同样吃总预算**：缓存读取也受剩余 `Resilience.TotalTimeout` 约束，存储挂起（自建存储不可用 / 网络分区）时按 `ECONNABORTED`（`IsTotalTimeoutExceeded`）抛出，不会把请求挂在查缓存上；调用方取消按 `ERR_CANCELED` 报出，存储自己抛的取消按「没命中」处理。实现 `IAxiosCacheStore.GetAsync` 时应当尊重传入的令牌。
- **落库同样吃总预算**：写入受 `StoreTimeout` 与剩余 `Resilience.TotalTimeout` 中较短者约束，超出就按「这次没缓存」处理（跟随者各自补发），不会把已经拿到的响应拖过预算；落库前预算就已耗尽时连正文都不准备。

**存储与运维**

- **强制清除**：`cache.ClearAsync()` 清空全部条目，`cache.RemoveAsync(key)` 摘掉单条（键由 `cache.KeyFor(config, uri)` 得到）；已发出的请求不受影响，完成后照常落库。
- **自定义存储收到完整策略**：`IAxiosCacheStore` 新增带 `AxiosCacheEntryPolicy` 的 `SetAsync` 重载（生效 / 配置 TTL、HTTP 新鲜期、龄期、解析后的 `Cache-Control`、`Date` / `Expires` / `Vary`）。
  - 默认实现转发到旧签名，已经写好的存储不用改；要按 `private` 拒写共享缓存、按 `Vary` 分区、或用 `stale-while-revalidate` 延长可服务窗口时覆写它即可。
  - `Configured` 模式下落库前不解析响应头，这些 HTTP 字段在存储读取时才按需解析；只读 TTL 的存储没有任何解析开销。
- **存储可换**：`AxiosCache` 自己不存条目 —— 存取全交给 `IAxiosCacheStore`（`GetAsync` / `SetAsync` ×2 / `RemoveAsync` / `ClearAsync`；`AxiosShortCircuit` 本身就是「状态码 + 头 + 原始字节」，可直接序列化）。
  - 默认实现是进程内 `MemoryCacheStore`；接 Redis 或跨实例共享缓存时实现这个接口，多个客户端挂同一个存储即跨实例命中。
  - 存储读写失败只等于「这次没缓存」，请求照发；读取挂起超出剩余 `Resilience.TotalTimeout` 则按 `ECONNABORTED` 报出（见上）。
- **命名空间是 `AxiosNet.Cache`**（与 `Retry/` / `Circuit/` 一致）：`AxiosCache` / `AxiosCacheOptions` / `CacheFreshnessMode` / `AxiosCacheEntryPolicy` / `CacheControlDirectives` / `IAxiosCacheStore` / `IAxiosCacheRevalidationStore` / `MemoryCacheStore` 都在里面。
- `AxiosShortCircuit` 仍在 `AxiosNet` —— 它不是缓存专用的，mock / 回放也在用。
- 回放 `response.Config` 会带着同一个缓存对象（它是共享状态，不是「那次请求的结果」）。

## 运行时与 AOT

类库三目标 `netstandard2.0` / `net6.0` / `net8.0`：.NET 8 及以上引用 `lib/net8.0`，.NET 6 / 7 引用 `lib/net6.0`，其余受支持的运行时引用 `lib/netstandard2.0`，由 NuGet 按兼容性自动选，无需配置。三份资产来自同一份源码；现代两份绑定到消费方运行时自带的 BCL，行为不随 TFM 变化。

`net6.0` / `net8.0` 两个目标都声明 `IsTrimmable`（自身裁剪 0 警告），`net8.0` 目标另开 `IsAotCompatible`，AOT 分析器 0 警告（`net6.0` 目标上 SDK 不提供该分析器）。兼容资产不参与裁剪 / AOT 分析。

### .NET Standard 2.0 兼容资产

`lib/netstandard2.0` 是功能子集，用来覆盖 **.NET Framework 4.7.2+ / .NET Core 2.0+（含 3.1）/ .NET 5**。
.NET Core 1.0 / 1.1 只支持到 `netstandard1.6`，同一份资产无法覆盖，因此不在支持范围内；
.NET 6 及以上永远取 `lib/net6.0` / `lib/net8.0`，不会落到兼容资产上，主路径行为不受影响。

它是同一份源码按 `NETSTANDARD2_0` 条件编译的产物，公共入口尽量保持一致，以下能力被移除或降级：

| 项 | `net6.0` / `net8.0` | `netstandard2.0` 兼容资产 |
| --- | --- | --- |
| 默认 HTTP 版本 | HTTP/2（可协商降级） | HTTP/1.1；显式要求 HTTP/2、HTTP/3 会在发送前拒绝 |
| 传输层 | `SocketsHttpHandler` | `HttpClientHandler`，只映射两边都有的选项（连接数上限、TLS 协议版本、压缩、重定向、Cookie、代理、凭据、证书校验回调） |
| 连接级选项 | 连接生命周期 / 并发上限 / h2 ping / `ConnectTimeout` / `ConnectCallback` / `SslOptions` | 只映射两边都有的项（`MaxConnectionsPerServer`、`EnabledSslProtocols`、压缩、重定向、Cookie、代理、凭据、证书校验回调）；`PooledConnectionLifetime` / `PooledConnectionIdleTimeout` / `ConnectTimeout` / `EnableMultipleHttp2Connections` / `KeepAlivePing*` 设为非默认值时在建池阶段以 `ERR_BAD_OPTION_VALUE` 拒绝（不静默忽略；用 `ConnectionPoolOptions.HandlerFactory` 接管默认档 Handler 时改由工厂决定）；**这些选项的默认值同样不生效**（默认 10 分钟连接轮换 / 2 分钟空闲回收 / 15 秒建连超时在该资产上都没有落点，连接生命周期与建连超时由 `HttpClientHandler` / 运行时决定）；`ConnectCallback` / `ConfigureSslOptions` / `KeepAlivePingPolicy` 不在兼容资产 API 上；`CreateDefaultHandler()` 返回 `HttpMessageHandler` |
| `HttpVersionPolicy` / HTTP/3 | 可用（h3 还需运行时支持） | 枚举形状保留但不写入请求（该枚举在 `AxiosNet` 命名空间，而非 `System.Net.Http`）；显式要求 HTTP/2 / HTTP/3 时无论策略都前置拒绝 |
| 请求体压缩 | gzip / deflate / Brotli | 仅 gzip；deflate / Brotli 在选项校验阶段以 `ERR_BAD_OPTION_VALUE` 拒绝 |
| 自动解压 | gzip / deflate / br | gzip / deflate（Framework 的枚举没有 Brotli） |
| `IContentSerializer` / 缓存 / 凭据接口 | 带默认接口实现 | 默认实现改成纯声明，自定义实现须显式实现全部成员（多目标消费方要为 `netstandard2.0` 单独写 `#if`：`IContentSerializer` 多 3 个成员、`IAxiosCredentialProvider` / `IApiCredentialProvider` 的 `Invalidate`、`IAxiosCacheStore.SetAsync(5 参)`） |
| 公开 `init` 属性 | `init`（编译期只能写一次） | 配置型属性退化为 `set`：`AxiosCacheOptions.Store`、`AccessTokenOptions.RefreshAsync`、`FormData.SourceName`。跨程序集的 `init` 在 .NET Framework 消费方**无法写入**（编译器只在 corlib 里解析 `IsExternalInit`，库内定义无论可见性都救不了），所以这几个必须是 `set`。公开 `record`（`ConnectionPoolStatistics` / `EndpointStatistics` / `ApiCredential` / `FormPartOverride`）保留 `init`：用主构造函数构造，`with` 表达式在 .NET Framework 消费方不可用 |
| 配置字典克隆 | `ConcurrentDictionary.Comparer` 随克隆保留 | 该属性在 ns2.0 契约和 .NET Framework 运行时里都不存在，克隆回退到 `StringComparer.Ordinal`（与普通 `Dictionary` 分支一致；用 `OrdinalIgnoreCase` 的并发字典克隆后会变 Ordinal） |
| 带令牌的 `HttpContent` / `Stream` override | 传播 `CancellationToken` | `HttpContent` 重载在进入 / 返回边界检查令牌（内部缓冲无法逐块中断）；`Stream` 的 Memory 重载与 `DisposeAsync()` 走兼容实现 |
| `DateOnly` / `TimeOnly` 格式化与解析 | 支持 | 不支持（这两个类型不在 ns2.0 契约里） |
| 方法级 `[AsyncMethodBuilder]`（池化 `ValueTask`） | 启用 | 不启用 |
| 裁剪 / AOT 标注与分析器 | 启用 | 只在编译期补空标注，不跑分析器 |

依赖只进 `netstandard2.0` 依赖组：9 个微软第一方 BCL 兼容包（`System.Text.Json`、`System.Net.Http.Json`、
`System.Memory`、`System.Threading.Tasks.Extensions`、`Microsoft.Bcl.AsyncInterfaces`、
`System.Diagnostics.DiagnosticSource`、`System.Reflection.DispatchProxy`、
`System.Runtime.CompilerServices.Unsafe`、`System.ComponentModel.Annotations`）；现代两个 TFM 的依赖组为空。
`AxiosNet.DependencyInjection` 同步提供 `lib/netstandard2.0` 资产。源生成器本身是 `netstandard2.0` 的 Roslyn 组件，
与消费方 TFM 无关，三份资产都随包分发。

**旧式 .NET Framework 项目需要绑定重定向**：这 9 个包在 `packages.config` 风格的工程里不会自动合并程序集版本，
默认 `AutoGenerateBindingRedirects` 为 false，运行期会报 `Could not load file or assembly 'System.Memory, Version=4.0.1.x'`。
在工程里加 `<AutoGenerateBindingRedirects>true</AutoGenerateBindingRedirects>` 与
`<GenerateBindingRedirectsOutputType>true</GenerateBindingRedirectsOutputType>`（SDK 风格工程默认已开启，无需处理）。

**在 .NET Framework 上用源生成器需要两件事**：`LangVersion` ≥ 9（`net472` 默认 7.3，`[ModuleInitializer]` 是 C# 9 特性），
以及一份可见的 `ModuleInitializerAttribute`——后者由生成器自动补在消费方编译里，不需要手工处理。

**响应字符集解析随宿主而定，不随资产而定**：`charset` 只经 BCL 的 `Encoding.GetEncoding` 解析
（`utf-8` / `us-ascii` / `iso-8859-1` 走内置实例）。.NET Framework 的 BCL 自带全部代码页，`gbk` / `gb2312` 等可直接解码；
.NET Core / .NET 5+ 需要先注册 `CodePagesEncodingProvider`（本库零依赖，不注册），这些字符集因此回退到 BOM / UTF-8。
要在现代运行时上恢复，消费方启动时调用一次 `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` 即可——
解析入口就是 `Encoding.GetEncoding`，注册后立即生效。

兼容资产有独立的运行时测试工程 `test/AxiosNet.Compat.Tests`：它只引用类库的 `netstandard2.0` 产物
（`SetTargetFramework`），因此 polyfill、shim 与全部 `#if NETSTANDARD2_0` 分支都会被真正执行。
宿主有两个：`net8.0`（跨平台）与 `net472`（Windows，真正跑在 .NET Framework 上，覆盖只有该运行时才有的分支——
例如 `Type.IsByRefLike` 缺失时的 `[IsByRefLike]` 兜底）。CI 在 `ubuntu-latest` 上跑 net8.0 宿主，
另有一个 `windows-latest` job 只跑 net472 宿主。

### 请求模型与接口：内置源生成器

包内自带 C# 源生成器（打包在 `analyzers/dotnet/cs`，引用即生效），编译期为每个带路由特性的请求模型烘焙元数据声明，并为接口级声明生成真实代理类（含继承接口、授权、multipart、流式、方法级编码），全部在模块初始化器里注册：

- **零配置**：不需要 `TrimmerRootAssembly`，也不需要手写注册；
- **两条路径同一份声明**：生成器只写事实（路由 / 成员 / 绑定 / 授权），校验与绑定计划仍由运行时共享构建器完成，JIT 与 AOT 的行为、错误文本一致；
- **运行时不再反射模型 / 接口**：请求模型把成员取值写成直接 getter 委托；接口代理只做「取参 → 调用共享绑定实现」，不 `MakeGenericMethod`；AOT 下未命中注册表会抛 `AxiosAotMetadataException`（带修复指引），不会静默丢字段；
- **JIT 专用的反射实现整段被裁掉**：接口声明 / 请求模型 / 自定义字典 / 对象形状的反射读取器与 `DispatchProxy` 回退（集中在 `Reflection/` 与 `WebAPI/Reflection/`），只写在 `RuntimeFeature.IsDynamicCodeSupported` 为 true 的分支里；ILC 折成常量 `false` 后整段不可达，连同它们的类型一起被裁剪，包消费方发布时不会为「只在 JIT 下用得到」的代码付体积；
- **接口代理的生成位置**：非泛型接口在声明处生成；闭合泛型接口（`IRepo<User>`）在 `Create<T>()` / `AddApiInterface<T>()` / `ValidateInterfaces(typeof(...))` 调用点生成，开放泛型声明本身（含外层类型为开放泛型的嵌套接口）只提示 `AXA011`（同一编译里已有闭合调用点时不报）。被引用程序集已经为同一接口生成过代理时，使用方按生成的类名识别并直接复用对方注册，不重复生成（重复注册会在启动期抛错）；只有被引用程序集没启用生成器时，使用方才在调用点生成；
- **回退路径**：没有生成元数据时 JIT 回退 `DispatchProxy`；Native AOT 下没有回退，直接抛 `AxiosAotMetadataException`；
- **前提**：消费方需 C# 9 及以上（`[ModuleInitializer]` 本身是 C# 9 特性；`net472` 工程的默认语言版本是 7.3，要显式设 `<LangVersion>`）；生成器本身是 `netstandard2.0`，随包分发（按 Roslyn 4.0.1 编译，.NET SDK 6.0.100 / VS 2022 17.0 及以上的编译器均可加载）；
- **.NET Framework / .NET Core 3.x 上的模块初始化器**：`System.Runtime.CompilerServices.ModuleInitializerAttribute` 不在这些运行时的 BCL 里，而生成代码写在**消费方**编译中，拿不到兼容资产里那份 `internal` 声明（直接引用是 CS0122）。生成器因此会在需要时就地补一份同名 `internal` 声明（既有声明公开可见或消费方自己已有——例如 PolySharp——时不补）；补位与兼容资产里的同名 `internal` 类型重复，生成文件已就地压掉 `CS0436`；
- **自定义路由特性**：`HttpApiAttribute` 的自定义子类无法在编译期执行构造器 → 警告 `AXA010` 并跳过生成；JIT 下仍走反射，AOT 下请改用 `[HttpApi("METHOD", "path")]` 或内置的 `[Get]` / `[Post]` 等；
- **关闭生成器**：`<AxiosNetSourceGen>false</AxiosNetSourceGen>`。JIT 下自动回退反射 / `DispatchProxy`；`PublishAot` 为 true 时给出警告 `AXA012`。

### 对象形状：params / 表单 / multipart 的展平

`params`、`form-urlencoded` 与 `multipart` 都要把对象展开成「键 → 值」。JIT 下按反射构建成员清单；启用源生成器时，形状在编译期烘焙并注册，AOT 下不再反射：

- **自动发现**：`[AxiosShape]` 类型、路由请求模型、接口方法参数类型、Axios 调用点实参（含 `PostFormAsync` 的 data）、`Params` / `Body` 赋值与对象初始化器、匿名对象创建；嵌套对象、集合元素、字典值递归展开；
- **命名类型**：用 `default(T)` 推断类型参数注册，取值委托是编译期写死的属性访问；只收录 public 读访问器（非 public getter 在生成与反射路径上一致跳过），ref struct / 指针成员无法装箱、两条路径同样跳过，`[JsonPropertyName]` / `[JsonIgnore]`、基类 `new` 隐藏与 `override` 的语义与反射路径逐字段一致；
- **匿名类型**：按「属性名 / 类型 / 顺序」生成结构相同的样本（`RegisterGeneratedShapeFromSample`）推断匿名类型参数；匿名元素数组写成 `new[] { ... }` 让编译器推断，结构不一致的匿名类型不会误命中；
- **自定义只读字典**：只生成枚举访问器（不注册 POCO 形状），`params` / 表单 / `multipart` 三条展平通道都走它；声明为接口类型时按接口登记，运行时沿具体类型的接口命中；访问器给出的 `null` 键在展平时跳过，回收 / 重试的遍历路径在 AOT 下没有访问器时停止下钻而不是抛错；
- **缺失形状显式失败**：AOT 下运行时类型没有形状就抛 `AxiosAotMetadataException`，消息带类型与字段路径（如 `位置：Payload`），不会静默丢字段。静态类型是 `object` / 接口 / 泛型参数的位置无法静态推断，可给具体类型标 `[AxiosShape]`，或手工调用 `AxiosMetadata.RegisterShape<T>()` / `RegisterShapeFromSample(...)` / `RegisterMap(typeof(T), ...)` 兜底；
- **不可见类型**：private 嵌套 / file-local / 跨程序集 internal 的类型无法被生成代码引用，跳过并给出 `AXA013` 警告（AOT 下用到会显式失败）。

| 诊断 | 级别 | 场景 |
| --- | --- | --- |
| `AXA001` | Error | 接口含无法代理的成员：属性 / 事件 / 静态成员 |
| `AXA002` | Error | 接口方法形态不支持：泛型方法、`ref` / `out` / `in`、ref struct / 指针参数、非 public 抽象成员、`CancellationToken` 附带绑定特性 |
| `AXA003` | Error | 必须且只能标注一个路由特性（模型与接口方法同一条）：标了多个，或接口方法没有路由 |
| `AXA004` | Error | 接口方法返回形态不支持（同步返回 / `ValueTask` 等） |
| `AXA005` | Error | 接口方法的 `[Stream]` 声明无效或帧格式值未定义 |
| `AXA006` | Error | `[ApiIgnore]` 与 Path / Query / Header / HeaderCollection / Part / Body 同用 |
| `AXA007` | Error | 路由特性存在但参数为空白或无法在编译期求值（与 `AXA003` 分开：修法不同） |
| `AXA008` | Error | `[Authorize]` 与 `[AllowAnonymous]` 标在同一层（模型或接口 / 方法） |
| `AXA009` | Error | 模型 / 接口无法生成元数据：静态类 / 不可访问 / 泛型 / ref struct / file-local / 成员 getter 非 public / 类型不可见 |
| `AXA010` | Warning | 模型 / 接口使用了自定义 `HttpApiAttribute` 子类（已跳过生成） |
| `AXA011` | Warning | 开放泛型接口（含外层类型为开放泛型的嵌套接口）没有闭合调用点（AOT 下 `Create` 会显式失败） |
| `AXA012` | Warning | `PublishAot` 下设置了 `AxiosNetSourceGen=false` |
| `AXA013` | Warning | 形状类型的访问级别对生成代码不可见（已跳过形状；AOT 下展平到该类型会显式失败） |
| `AXA014` | Error | 接口方法的编码声明无效：`[BodyContentType]` 为空白 / 含控制字符、`[QueryCollectionFormat]` 值未定义，或与 `[Multipart]` 同用 |
| `AXA015` | Error | `[Format]` 声明无效：格式化字符串为空白，或格式化函数不存在 / 不是 `public static`、参数列表不是 `(object?, ValueTextChannel)`、返回值不是 `string` |

`System.Text.Json` 的序列化元数据仍需要按需注册（源生成 `JsonSerializerContext`）：

```csharp
[JsonSerializable(typeof(Person))]
internal partial class ApiJsonContext : JsonSerializerContext { }

var client = new AxiosClient(new AxiosConfig
{
    Serialization = new()
    {
        JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = ApiJsonContext.Default
        }
    }
});
```

`TypeInfoResolver` 是 .NET 7 才有的属性：`net6.0` 目标（被 .NET 6 / 7 应用引用）的 `JsonSerializerOptions` 上没有它，因此那份资产无法接入源生成元数据，AOT 下请改用自定义 `IContentSerializer`，或把目标框架提升到 `net8.0`。反射被关掉时两份资产报同一种 `NotSupportedException`，`net6.0` 的消息会额外点明这一点。

`JsonSerializerDefaults.Web` 不能省：换成裸 `new JsonSerializerOptions()` 会把 camelCase 换成属性原名，请求 / 响应字段名跟着变，而源生成上下文里的 `JsonSourceGenerationOptions` 不会替你补回来（`AxiosJson.Options` 的默认值就是 Web）。

`[Format]` 的 JSON 桥（`AxiosJsonFormat.ApplyTo`）同样不可用：它按属性类型构造泛型 `JsonConverter` 并读取属性特性，方法上已标注 `[RequiresDynamicCode]` / `[RequiresUnreferencedCode]`。AOT 下用 `[Format]` 只能作用于库自己转文本的通道（路径 / 查询 / 请求头 / 表单 / multipart / 裸标量正文），JSON 文档内部请改用自定义 `JsonConverter` 或直接换个 DTO。

AOT 下缺少元数据不会静默丢字段，而是抛出带修复指引的异常：

- 请求模型 / 接口缺元数据 → `AxiosAotMetadataException`（消息含类型名与修复方式；也可用隐藏 API `AxiosApiMetadata.RegisterRequestModel` / `RegisterInterface` 手工注册声明兜底）。
- `System.Text.Json` 缺类型元数据 → `NotSupportedException`，提示把类型加入 `JsonSerializerContext` 并设置 `TypeInfoResolver`；匿名类型无法写入 `[JsonSerializable]`，因此消息会直接建议改用命名类型或 params / 表单。
- `XmlContentSerializer` 与 `AxiosDynamic` 的 dynamic 成员语法 → `PlatformNotSupportedException`（`AxiosDynamic` 的索引访问与 `IDictionary` 接口仍可用）。

AOT 下不可用的能力：

- `dynamic` 成员语法（`data.name`）与运行期才确定的类型。
- `XmlContentSerializer`。
- 程序集扫描：`ValidateModels(Assembly)` / `ValidateInterfaces(Assembly)` 依赖反射枚举，AOT 下抛 `PlatformNotSupportedException`。改用生成的 `AxiosNet.Generated.AxiosNetValidation_<程序集名>.ValidateAll()`（覆盖本程序集里源生成器生成过元数据的全部请求模型与接口），或按类型调用 `ValidateModels(typeof(A), ...)` / `ValidateInterfaces(typeof(IA), ...)`。
- 接口代理的回退路径：未生成 / 未注册时的 `DispatchProxy` 依赖 Reflection.Emit，仅 JIT。

生成的类名把程序集名里的非标识符字符换成 `_`（`AxiosNet.Aot` → `AxiosNetValidation_AxiosNet_Aot`）；本程序集没有任何可生成的模型 / 接口时不会生成该类。匿名对象作为 params / 表单值不需要额外配置：生成器会为结构烘焙样本（见上文「对象形状」）。

容器注册与冻结都是可选加固。

引用了 `AxiosNet.DependencyInjection` 时，生成器会额外产出 `AxiosNetGeneratedDependencyInjection.AddAxiosGeneratedApiInterfaces(services[, siteName])`，把本程序集里声明过路由的接口逐个按单例注册。它逐条调用公开的 `AddApiInterface<T>()`，站点冲突 / 命名站点 / 单例语义与手写完全同一份实现，区别是类型在编译期绑定 —— 接口改名会让它编译失败，而不是运行时少注册一个服务。

启动末尾想锁死元数据可调用隐藏 API `AxiosMetadata.Freeze()`：之后任何真正写入（新程序集的模块初始化器、手工注册）都会抛错并指出类型与程序集；等价重复注册仍是空操作。不调用它完全不影响使用。

**已知限制**：在可卸载 `AssemblyLoadContext`（插件热重载）里，第二次注册会被静默丢弃，代理工厂仍指向先注册那次加载的程序集——重载插件时不要依赖注册表自动换新，改用独立的进程或卸载后重新进入新的 `AssemblyLoadContext` 边界。

## 参考

> 从 1.x 升级到 1.5 的配置改名与行为收紧，见 [1.5 破坏性变更](docs/breaking-changes-1.5.md)。

### 能力地图

按能力分四组，点能力名直达对应章节。这里只给入口，边界条件都在章节里。

**请求与响应**

| 能力 | 一句话 | 怎么做 |
| --- | --- | --- |
| [四种调用形态](#快速上手) | 原文 / `JsonElement` / `dynamic` / 强类型 | `GetAsync<string>` / `GetAsync()` / `GetAsync<dynamic>` / `GetAsync<T>` |
| [声明式 API](#声明式客户端) | 用模型或接口描述请求，`ApiClient` 生成调用；有 OpenAPI 文档时直接生成 | `[Get("users/{id}")] class X : IApiRequest<User>` / `client.Create<T>()` / `axiosnet-gen`（[从 OpenAPI 导入](#从-openapi-导入)） |
| [客户端实例](#客户端) | 一套 baseURL + 超时 + 默认头 | `Axios.Create(c => ...)` / `new AxiosClient(config)` |
| [只拼 URL](#方法与-url) | 不发请求，拿最终 URL | `client.GetUri(c => ...)` |
| [查询参数](#查询参数-params) | 对象 / 字典 / 集合 / 已编码串 | `c.Params = new { page = 2 }` |
| [请求体](#请求体与-contenttypejson-默认) | 编码只看 `ContentType`，不猜类型 | `c.ContentType = AxiosContentType.FormUrlEncoded` |
| [表单与上传](#表单与文件上传) | multipart 字段 + 文件，或裸流直传 | `PostFormAsync(...)` / `FormFile` / `Stream` |
| [流式请求体](#流式请求体) | 异步序列边算边发，不缓冲整段 | `data = IAsyncEnumerable<byte[]>`（或 `ReadOnlyMemory<byte>` 块） |
| [响应类型](#响应类型与下载) | JSON / 文本 / 字节 / 流 / dynamic 五种 | `c.ResponseType = ResponseType.Stream` |
| [流式响应](#流式响应) | NDJSON / SSE / JSON 数组 → `IAsyncEnumerable<T>` | `client.RequestStreamAsync<T>(req, AxiosStreamFormat.ServerSentEvents)` |
| [超时与取消](#超时与取消) | 单次尝试预算 + 整条请求总预算 + `CancellationToken` | `c.Timeout = ...` / `c.Resilience.TotalTimeout = ...` / `cancellationToken:` |
| [凭据与令牌管理](#凭据与令牌管理) | 统一凭据扩展点与落点（`Authorization` 头 / 任意头 / 查询参数）、令牌缓存、提前刷新、并发刷新单飞、401 重放 | `ApiClient.CredentialProvider` / `AccessTokenManager` |

**流量治理**

| 能力 | 一句话 | 怎么做 |
| --- | --- | --- |
| [进度与限速](#进度限速与大小上限) | 上传下载进度、字节限速、体积上限 | `c.Observability.OnDownloadProgress` / `c.Transport.MaxRate` |
| [在途并发闸门](#在途并发上限) | 限制同时发出的请求条数，排队深度可设上限 | `c.Resilience.Concurrency.MaxInFlight` + `MaxQueue` / `QueueTimeout` |
| [弹性重试](#重试) | 指数退避 / 抖动 / `Retry-After` / 幂等门禁 / 全局重试预算 | `c.Resilience.Retry.Policy = RetryPolicy.Exponential(...)` |
| [熔断](#熔断) | 按 origin（默认 `host:port`）计数的 Closed/Open/HalfOpen 状态机：滚动窗口失败率 + 连续失败两条打开规则，恢复期慢启动 | `c.Resilience.Circuit.Policy = new CircuitBreaker()` |
| [并发对冲](#并发对冲) | 同一份请求并发多条分支，先成功的胜出 | `c.Resilience.Hedging.MaxHedges = 1` |
| [备用端点](#备用端点) | 当前端点用不了时换镜像重发；失败端点在客户端内跨请求冷却，主端点挂掉时不再每条请求都先撞它 | `c.Resilience.Failover.Endpoints = new[] { new Uri("https://mirror") }` |
| [幂等键](#幂等键) | 一个逻辑请求一个键，重试 / 换端点 / 对冲共用 | `c.Resilience.Idempotency.Enabled = true` |
| [尝试管线](#尝试管线) | 一次尝试 = 可组合中间件链，可插可重排 | `Axios.Create(..., pipeline: AxiosAttemptPipeline.Default.Append(...))` |
| [短路响应](#短路响应) | 不发请求，直接交付响应（缓存 / mock / 回放） | `c.ShortCircuit = new AxiosShortCircuit { ... }` |
| [响应缓存](#响应缓存) | 按 URL 与身份缓存 2xx 响应，命中不发请求；同键并发只发一次；过期条目走 `ETag` / 304 条件复用 | `client.Defaults.Caching = new AxiosCache(...)` |

**数据与错误**

| 能力 | 一句话 | 怎么做 |
| --- | --- | --- |
| [统一错误模型](#错误处理) | 所有失败都是 `AxiosException`，带稳定 `Code` | `catch (AxiosException ex) { ex.Code }` |
| [拦截器](#拦截器) | 请求 / 响应双向，可异步 | `client.Interceptors.Request.Use(...)` |
| [数据转换](#数据转换) | axios 同构的两条 transform 链 | `c.TransformRequest.Add(...)` |
| [序列化可替换](#序列化) | JSON（默认）/ XML 内置，任意格式可插 | `AxiosContent.Register(...)` / `Serialization.BodySerializers` |
| [请求体压缩](#请求体压缩) | gzip / deflate / br，压缩后自动写 `Content-Encoding` | `c.Compression = AxiosCompression.Gzip` |
| [可观测性](#可观测性) | `ActivitySource` + `Meter` + `DiagnosticListener` | `AddSource("AxiosNet")` / `AddMeter("AxiosNet")` |

**连接与工程化**

| 能力 | 一句话 | 怎么做 |
| --- | --- | --- |
| [连接池](#连接池) | 每个 origin 一条调优过的 Handler，共享长连接 | 默认自建；`new HttpConnectionPool(...)` 或 `HttpConnectionPool.GetOrCreate("key")` 可共享 |
| [传输档](#传输档) | 一条池多条出口：按请求选代理 / 凭据 / 客户端证书 / Cookie 会话，各档连接与会话互不干扰 | `ConnectionPoolOptions.AddTransport("egress-a", ...)` + `c.Transport.Profile` |
| [HTTP 版本](#http-版本) | 默认 HTTP/2 可降级，支持 HTTP/3 | `c.Transport.HttpVersion` |
| [自定义 Handler](#自定义-handler) | 套 `DelegatingHandler`，或完全接管传输层 | `ConnectionPoolOptions.HandlerFactory` |
| [依赖注入](#依赖注入) | 单例注册，多站点命名解析 | `AddAxiosClient(...)` / `AddApiClient(...)` |
| [Native AOT](#运行时与-aot) | 已声明 `IsTrimmable`（裁剪 0 警告），`net8.0` 目标另开 `IsAotCompatible`（AOT 分析器 0 警告），内置请求模型 / 接口代理源生成器，零 `TrimmerRootAssembly` | 引用即生效；序列化另配 `JsonSerializerContext` |

### 与 axios 对照

| axios (JS) | AxiosNet (.NET) |
| --- | --- |
| `axios(config)` / `axios(url, config)` | `client.RequestAsync(new AxiosConfig { ... })` / `client.GetAsync(url, c => ...)` |
| `axios.get/post/put/patch/delete/head/options` | `GetAsync` / `PostAsync` / `PutAsync` / `PatchAsync` / `DeleteAsync` / `HeadAsync` / `OptionsAsync` |
| `axios.create(config)` | `Axios.Create(config)` / `Axios.Create(config, sharedPool)` |
| `axios.getUri(config)` | `client.GetUri(config)` / `client.GetUri(c => ...)` |
| `axios.postForm / putForm / patchForm` | `PostFormAsync` / `PutFormAsync` / `PatchFormAsync` |
| `axios.defaults` | `Axios.Defaults` / `client.Defaults` |
| `res.data / status / statusText / headers / config` | `Data` / `Status` / `StatusText` / `Headers` / `Config`（另有 `ProtocolVersion` / `Request`） |
| `interceptors.request/response.use` / `eject` | `Interceptors.Request/Response.Use` / `Eject` |
| `config.params` / `config.data` | `c.Params` / `c.Body`（编码由 `c.ContentType` 决定） |
| `responseType: 'json' / 'text' / 'arraybuffer' / 'stream'` | `ResponseType.Json` / `Text` / `ArrayBuffer` / `Stream` |
| `validateStatus` / `timeout` / `AbortController` | `c.ValidateStatus` / `c.Timeout` / `c.CancellationToken` |
| `auth: { username, password }` | `c.Transport.Auth = AxiosCredential.Basic(user, pass)` |
| —（令牌 / 401 刷新靠自己写拦截器） | 声明式 `ApiClient.CredentialProvider` + `AccessTokenManager`（自动刷新 / 401 重放） |
| `withCredentials` / `proxy` / `maxRedirects` | `Transport` 子对象同名属性（仅实例级） |
| `maxContentLength` / `maxBodyLength` / `maxRate` | `Limits.MaxResponseBytes` / `Limits.MaxRequestBytes` / `Transport.MaxRate` |
| `onUploadProgress` / `onDownloadProgress` / `timeoutMessage` | `Observability` 子对象同名属性 |
| `paramsSerializer` / `formSerializer` | `Serialization.QueryEncoder` / `Serialization.FormFieldName` |
| `transformRequest` / `transformResponse` | 同名属性 |
| `AxiosError.code` / `isAxiosError` / `isCancel` | `AxiosException.Code` / `Axios.IsAxiosError` / `Axios.IsCancel` |
| `axios-retry` 插件 | `c.Resilience.Retry.Policy` / `c.Resilience.Retry.OnRetry` |
| —（需接 opossum 之类） | `c.Resilience.Circuit.Policy` / `c.Resilience.Circuit.OnRejected` |
| —（需接 cockatiel 之类） | `c.Resilience.Hedging.MaxHedges`（并发对冲）、`c.Resilience.Failover.Endpoints`（备用端点） |
| —（需接 OTel JS SDK） | 内置 `ActivitySource` / `Meter` / `DiagnosticListener` |
| —（axios-cache-interceptor 之类） | `c.Caching = new AxiosCache(...)`（内置响应缓存 + 单飞）、`c.ShortCircuit`（手写缓存命中 / mock / 离线回放） |
| —（无对应） | `HttpConnectionPool`、声明式 `ApiClient`、`RequestStreamAsync`、`Resilience.Concurrency.MaxInFlight` / `MaxQueue` / `QueueTimeout`、`IAsyncEnumerable` 流式请求体 |

与 axios 的有意差异：

1. 方法名带 `Async` 后缀；`timeout` 用 `TimeSpan`；取消用 `CancellationToken`。
2. 请求体编码由 `ContentType` 决定（默认 JSON），不按 data 类型推断——字典不会自动变表单。
3. 非泛型文本响应的 `Data` 是 `JsonElement`（非法 JSON 回退原文），二进制是 `byte[]`；要 JS 式访问用 `GetAsync<dynamic>`。
4. 实例级选项按请求写**不同的值**会立即抛 `ERR_BAD_OPTION_VALUE`（axios 是静默不生效）。
5. `params` 用 `Uri.EscapeDataString` 严格编码（空格 `%20`，不是 `+`）。
6. `transformRequest` / `transformResponse` 的元素类型是 `object?`（C# 无法像 JS 那样丢失类型再收窄）。
7. 请求头写入即校验（RFC 7230 token、禁止 CR/LF），非法直接抛 `ArgumentException`（声明式绑定中的同类错误统一包装为 `ERR_BAD_REQUEST`，原异常在 `Cause`）。
8. `MaxRate` / 进度回调实现在 `HttpContent` 与 `Stream` 层（BCL 无原生支持）。
9. `Timeout` 上限约 49.7 天，超出收敛到上限；真正无限超时用 `Timeout = Zero`。

### 依赖注入

```csharp
using AxiosNet.DependencyInjection;
using AxiosNet.WebAPI.DependencyInjection;

builder.Services.AddAxiosClient(config =>                    // 只要核心客户端
{
    config.BaseAddress = "https://api.example.com";
    config.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddAxiosClient(                             // 顺带换掉尝试管线
    configure: config => config.Timeout = TimeSpan.FromSeconds(10),
    pipeline: AxiosAttemptPipeline.Default.Append(auditAttempt));

builder.Services.AddApiClient("https://api.example.com",     // 声明式，自建底层客户端
    configureDefaults: config => config.Timeout = TimeSpan.FromSeconds(10),
    configure: client => client.CredentialProvider = CredentialProviders.Fixed(token));

builder.Services.AddApiClient(                               // 声明式做门面，复用上面的 AxiosClient 单例
    configure: client => client.CredentialProvider = CredentialProviders.Fixed(token));

builder.Services.AddApiClient("a", "https://a.example.com", configure: c => c.CredentialProvider = providerA);   // 多站点
builder.Services.AddApiClient("b", "https://b.example.com", configure: c => c.CredentialProvider = providerB);
```

| 扩展方法 | 注册内容 |
| --- | --- |
| `AddAxiosClient(configure, pipeline)` | 单例 `AxiosClient`（可选尝试管线） |
| `AddApiClient(baseAddress, configureDefaults, configure)` | 单例 `ApiClient`（自建底层客户端） |
| `AddApiClient(configure)` | 单例 `ApiClient`（复用容器里的 `AxiosClient`） |
| `AddApiClient(name, baseAddress, ...)` | 单例 `IApiClientResolver` + 命名站点 |
| `AddApiInterface<T>()` / `AddApiInterface<T>(name)` | 单例接口代理（默认 / 命名站点） |

- TryAdd 语义：已注册不重复注册，不会建出第二条池。
- 容器里的 `IApiCredentialProvider` / `IApiErrorFactory` 自动挂到 `ApiClient`，`configure` 里显式设置的优先。
- 生命周期归容器：`using` 掉注入的 `ApiClient` 不会释放共享的底层客户端。
- 多站点用 `IApiClientResolver.Get(name)` / `TryGet` / `Names`；.NET 8+ 也可用键名服务（`AddKeyedSingleton` + `[FromKeyedServices]`）。
- 接口级声明可直接注册：`services.AddApiInterface<IUserApi>()`（取未命名 `ApiClient`）或 `AddApiInterface<IUserApi>("a")`（取命名站点）；代理按单例复用，来自源生成器烘焙的代理（JIT 与 Native AOT 同一路径），未生成时才在 JIT 下回退 `DispatchProxy`，AOT 下解析即显式失败。
- 一个接口只能绑一个站点：换站点再注册一次（含未命名 ↔ 命名）在注册期抛 `InvalidOperationException`，不会静默丢掉第二条；两个站点共用方法时定义派生接口再注册到另一个站点。

### 示例项目

[`samples/AxiosNet.Simple`](samples/AxiosNet.Simple) 是离线可运行的控制台示例，自带 `TcpListener` 演示服务器，共 **56 项**：

```bash
dotnet run --project samples/AxiosNet.Simple
```

启动后是交互菜单（反射列出全部示例，输入编号运行，`a` 跑全部，`q` 退出）。

| 套件 | 项数 | 覆盖 |
| --- | ---: | --- |
| 核心 | 39 | 静态门面与 `Axios.Reset`、实例创建、JSON / 表单 / multipart、params、Basic Auth、dynamic 请求与响应、text / arraybuffer、拦截器、错误与取消、连接池、上传下载、getUri 与大小限制、进度限速、自定义 Handler、序列化替换（Newtonsoft / XML / 二进制）、DNS 切换、Bearer 令牌、请求签名、弹性重试、全局重试预算、幂等键、HTTP/2·3、可观测性、熔断、短路响应、响应缓存、命名共享池、尝试管线、并发对冲与备用端点、排队上限与背压、传输档、缓存身份键、响应对象上的正文 |
| 声明式 | 17 | GET 详情 / 列表、POST 创建、统一凭据扩展点与 `[AllowAnonymous]` / 本客户端自举取令牌、错误处理、全局日志、签名验签、流式响应（SSE / JSON 数组）、multipart 上传、单帧上限、接口级声明、接口级授权与校验、接口级 multipart、接口级流式、方法级编码（表单正文 / 查询集合格式）、值格式化（`[Format]` / `Serialization.ValueText` 优先级）、OpenAPI 导入（`axiosnet-gen` 默认生成请求模型，含 tag 分组 / 授权 / 表单 / multipart / 流式） |

另有两个示例：

- [`samples/AxiosNet.Simple.DependencyInjection`](samples/AxiosNet.Simple.DependencyInjection)：Generic Host 六个 DI 场景。
- [`samples/AxiosNet.Aot`](samples/AxiosNet.Aot)：Native AOT + 裁剪，自带回环服务。

<a id="native-aot"></a>

### 构建与测试

```bash
dotnet build AxiosNet.sln
dotnet test AxiosNet.sln
dotnet build AxiosNet.sln -c Release
```

主库与 `AxiosNet.DependencyInjection` 各编译 `netstandard2.0` / `net6.0` / `net8.0` 三份资产；CLI、测试与示例项目仍是 `net6.0` / `net8.0` 双目标（它们不是运行时库）。

| 测试项目 | 用例数 | 目标框架 | 执行次数 |
| --- | ---: | --- | ---: |
| `AxiosNet.Tests`（核心 + 声明式） | 2507 / 2496 | `net8.0` / `net6.0` | 5003 |
| `AxiosNet.DependencyInjection.Tests` | 66 | `net8.0` / `net6.0` | 132 |
| `AxiosNet.Cli.Tests`（OpenAPI 生成） | 74 | `net8.0` | 74 |
| `AxiosNet.Compat.Tests`（netstandard2.0 兼容资产） | 47 / 42 | `net8.0` / `net472` | 89 |
| **合计** | **2694 / 2678** | | **5298** |

表里每格是「同一份源码在两个 TFM 上各跑一遍」的用例数，斜杠两侧不是「总数 / 通过数」。

行覆盖率 **92.1%**、分支覆盖率 **87.6%**（`AxiosNet.Tests` / net8.0，`--collect:"XPlat Code Coverage"`，排除性能与分配类用例）：

```bash
dotnet test test/AxiosNet.Tests/AxiosNet.Tests.csproj -f net8.0 --collect:"XPlat Code Coverage" \
  --filter "FullyQualifiedName!~ConcurrencyPerformanceTests&FullyQualifiedName!~AllocationSweepTests&FullyQualifiedName!~AllocationTests&FullyQualifiedName!~HeadersCopyOnWriteTests"
```

普通 `dotnet test` 仍然全跑（含被排除的这几类）。

#### Native AOT 验证

```bash
# 发布并运行 AOT 示例
dotnet publish samples/AxiosNet.Aot -c Release -r linux-x64 -p:TrimmerSingleWarn=false \
  -p:WarningsAsErrors=IL2026%3BIL3050%3BIL3051%3BIL2070%3BIL2072%3BIL2075%3BIL2087%3BIL2098
./samples/AxiosNet.Aot/bin/Release/net8.0/linux-x64/publish/AxiosNet.Aot

# 包消费验证：pack → 只引用包的临时项目 → AOT 发布 → 运行
bash eng/aot-package-smoke.sh
```

#### 用例按功能分类

`test/AxiosNet.Tests` 的目录结构就是功能清单——找用例先看目录，再找文件名：

| 目录 | 覆盖范围 |
| --- | --- |
| `Client/` | 静态门面 `Axios`、实例选项与默认值、默认头、配置合并与六个配置分组 |
| `Requests/` | URL 与 params、请求体（JSON / 表单 / multipart）、响应解析与缓冲、流式响应、超时取消、Content-Type 解析 |
| `Governance/` | 并发闸门、进度回调与限速 |
| `Resilience/` | 重试策略与计时、总预算（`Resilience.TotalTimeout`）、熔断状态机、短路响应 |
| `Caching/` | 响应缓存与单飞、条件请求（`ETag` / 304）与 `IAxiosCacheRevalidationStore`、`IAxiosCacheStore` 契约与自建存储、键规则与身份隔离、命中路径 |
| `Serialization/` | `IContentSerializer` 契约与注册表、JSON 默认选项 |
| `Pipelines/` | 请求 / 响应拦截器、`transformRequest` / `transformResponse`、尝试管线（内置顺序、插入位置、重排校验）、并发对冲、备用端点与端点健康记忆 |
| `Connectivity/` | 连接池（含命名共享池、命名传输档）、Handler 注入、HTTP/2·3 协商与降级、重定向、本机 h3 能力探测 |
| `Diagnostics/` | Activity / Meter / DiagnosticListener 三路遥测、尝试审计事件 |
| `Errors/` | 错误模型、错误码、`ToJson` 快照 |
| `Regression/` | 分配护栏、并发吞吐 |
| `WebAPI/` | 声明式模型绑定与校验、授权、流式、接口级代理与启动期校验、请求模型源生成器（生成路径 vs 反射路径逐字段对照） |
| `Architecture/` | 架构护栏：核心不得引用 `WebAPI/` 扩展（扫源码断言，破坏「可整体删除」即失败） |
| `Infrastructure/` | 测试基建：HTTP 服务器、模型、遥测探针、独占集合 |

#### 性能基准

[`test/AxiosNet.Benchmarks`](test/AxiosNet.Benchmarks) 是独立 BenchmarkDotNet 项目，用于测量请求管线、配置合并、序列化、流式分帧与大正文等各项成本：

```bash
dotnet run -c Release --project test/AxiosNet.Benchmarks                       # 交互菜单
dotnet run -c Release --project test/AxiosNet.Benchmarks -- --filter '*Merge*'  # 参数直传 BDN
dotnet run -c Release --project test/AxiosNet.Benchmarks -- --filter '*' --isolated
```

默认**同进程**执行（不生成临时项目、不重建）；`--isolated` 用每用例独立进程跑（临时项目 + 重建），两种方式的绝对耗时不能混比（`Allocated` 两者都可信）。

| 组 | 热点 | 基线 |
| --- | --- | --- |
| `RequestPipelineBenchmarks` | 请求全链路（真实回环 socket） | `HttpClient_GetString` |
| `ConcurrentPipelineBenchmarks` | 8 条在途时的每请求成本 | `HttpClient_Concurrent` |
| `ConfigMergeBenchmarks` | `MergeConfig` / 请求头合并 | `GetUri_NoMerge` |
| `ConfigPrimitiveBenchmarks` | `AxiosConfig.Clone` / `AxiosHeaders` 原语 | —（看绝对值） |
| `FormEncodingBenchmarks` | 表单展平 + 百分号编码 | `PostForm_FlatAscii` |
| `DynamicDeserializationBenchmarks` | `dynamic` 反序列化 | `Typed` |
| `StreamingFramingBenchmarks` | 流式分帧（NDJSON / SSE / JSON 数组） | `Ndjson` |
| `LargeBodyUploadBenchmarks` | 大正文上传（1 / 8 / 32MB：`byte[]` / `ReadOnlyMemory` / 流 / 异步序列，真实回环 socket） | `HttpClient_Upload_ByteArray` |
| `LargeBodyDownloadBenchmarks` | 大正文下载（1 / 8 / 32MB：缓冲成 `byte[]` vs 流式读完） | `HttpClient_Download_Buffered` |
| `LargeBodyJsonBenchmarks` | 大正文 JSON 响应（1 / 8 / 32MB：`byte[]` / 强类型 / `JsonElement`） | `HttpClient_GetBytes` |

读法与分组说明见 [`test/AxiosNet.Benchmarks/README.md`](test/AxiosNet.Benchmarks/README.md)。

跨实现横向对比见 [`test/AxiosNet.Comparison`](test/AxiosNet.Comparison)：

- 同一回环服务器、同一负载（默认 8 并发 × 1s × 10 轮、每轮轮转顺序），对比裸 `HttpClient` / Refit / RestSharp / Flurl / AxiosNet 的 RPS、p50 / p99 / max 与每请求分配，输出控制台表 + Markdown / CSV 报告。
- 加 `--min-ratio` / `--max-alloc-ratio` 可当回归门禁；不设阈值时只出数字、不判定。
- 结论版（功能 / 性能 / 易用性 + 选型建议）见 [`docs/http-client-comparison.md`](docs/http-client-comparison.md)。

### 项目结构

```
.
├─ src/
│  ├─ AxiosNet/                       # 类库（netstandard2.0 / net6.0 / net8.0 三目标）
│  │  ├─ Axios.cs                     # 静态门面
│  │  ├─ AxiosClient*.cs              # 实例 + 请求管线 + 快捷方法
│  │  ├─ AxiosConfig*.cs              # 配置（顶层 + 六个分组）
│  │  ├─ AxiosHeaders.cs              # 请求头
│  │  ├─ AxiosResponse.cs / AxiosException.cs / AxiosErrorCode.cs / AxiosShortCircuit.cs
│  │  ├─ AxiosDynamic.cs / AxiosContent.cs / AxiosJson.cs / FormFile.cs（顶层公共类型）
│  │  ├─ Compatibility/               # netstandard2.0 兼容层：编译期 polyfill / global using 别名 / BCL 扩展 / 平台 helper
│  │  ├─ Cache/                       # AxiosCache（响应缓存 + 单飞）/ IAxiosCacheStore / MemoryCacheStore
│  │  ├─ Interceptors/                # 请求 / 响应拦截器
│  │  ├─ Pipeline/                    # 尝试中间件链：AxiosAttemptPipeline + 内置重试 / 备用端点 / 熔断 / 对冲 / 闸门 / 审计
│  │  ├─ Retry/                       # IAxiosRetryPolicy / RetryPolicy / RetryBudgetPolicy
│  │  ├─ Circuit/                     # IAxiosCircuitPolicy / CircuitBreaker
│  │  ├─ Serialization/               # IContentSerializer + JSON / XML 实现
│  │  ├─ Telemetry/                   # ActivitySource / Meter / DiagnosticListener
│  │  ├─ Pooling/                     # HttpConnectionPool + 池选项 / 统计 + Handler 注入
│  │  ├─ Metadata/                    # 生成元数据注册入口（对象形状 / 字典映射 / 冻结）+ 形状构建器
│  │  ├─ WebAPI/                      # 声明式 API 客户端（可选扩展，可整体删除；Attributes/ 公开特性，Metadata/ 公开注册入口，Internal/ 共享契约，Reflection/ 仅 JIT）
│  │  ├─ Reflection/                  # 对象形状与 STJ 的反射回退（只在 JIT 下可达，AOT 镜像里整块裁掉）
│  │  └─ Internal/                    # URL、正文编码、响应读取、流量计量
│  ├─ AxiosNet.SourceGen/             # 源生成器（netstandard2.0 分析器）：与 Reflection/ 对称的生成期路径
│  ├─ AxiosNet.DependencyInjection/   # DI 扩展（独立包，同为三目标；Compatibility/ 放本包的 polyfill）
│  └─ AxiosNet.Cli/                   # 设计期工具 axiosnet-gen：OpenAPI / Swagger → 声明式 C#（见「从 OpenAPI 导入」）
├─ samples/                           # Simple（56 项）/ DI / Aot
├─ eng/                               # CI 脚本（AOT 包消费 smoke，见「Native AOT 验证」）
└─ test/
   ├─ AxiosNet.Tests/                  # 核心 + 声明式（目录 = 功能分类，见「构建与测试」）
   ├─ AxiosNet.DependencyInjection.Tests/
   ├─ AxiosNet.Cli.Tests/              # CLI：fixtures 快照 / 生成代码编译 / 确定性与 --check / 覆盖保护与清单跟踪 / --dry-run
   ├─ AxiosNet.Benchmarks/             # BenchmarkDotNet（单实现分项成本）
   └─ AxiosNet.Comparison/             # 跨实现横向对比（RPS / p99 / 分配，可选门禁）
```

公开特性统一收在 `WebAPI/Attributes/`（命名空间 `AxiosNet.WebAPI.Attributes`），其余公开类型留在 `WebAPI/` 根。

## License

[MIT](LICENSE)