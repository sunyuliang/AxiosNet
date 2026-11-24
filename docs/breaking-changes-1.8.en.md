# AxiosNet 1.8 breaking changes

This is the **1.7 → 1.8** migration reference for callers upgrading from `AxiosNet` 1.7.x to 1.8.x; it matches the public surface shipped since 1.8.0.

All three changes remove a layer rather than add one: declarative calls deliver the body by default, a shortcut's second argument is decided by the verb alone, and a member's place in the body is spelled by the shape of `[Body]`.

## 1. Declarative calls return the body

`RequestAsync` now returns `Task<TResponse>`; for status code / response headers / the request message use the new `RequestFullAsync` (two overloads, on `ApiClient` and as `AxiosClient` extension methods).

| 1.7 | 1.8 |
| --- | --- |
| `Task<AxiosResponse<T>> ApiClient.RequestAsync<T>(IApiRequest<T>, …)` | `Task<T> ApiClient.RequestAsync<T>(IApiRequest<T>, …)` |
| — | `Task<AxiosResponse<T>> ApiClient.RequestFullAsync<T>(IApiRequest<T>, …)` (new) |
| `Task<AxiosResponse<T>> AxiosClientDeclarativeExtensions.RequestAsync<T>(…)` | `Task<T> …RequestAsync<T>(…)` + `…RequestFullAsync<T>(…)` (new) |
| `var user = (await client.RequestAsync(new GetUserRequest { Id = 5 })).Data!;` | `var user = await client.RequestAsync(new GetUserRequest { Id = 5 });` |
| `var rsp = await client.RequestAsync(request);` (reads `rsp.Status` / `rsp.Headers` / `rsp.Request`) | `var rsp = await client.RequestFullAsync(request);` |

- An empty body yields `default(TResponse)`, matching the interface-style `Task<T>` shape; to tell "empty body" apart use `RequestFullAsync` (`Data` is `null`) or `DataOrThrow()`.
- `RequestStreamAsync` is unchanged: a stream must hand back the response (frame disposal ownership lives on it).
- Interface proxies from `Create<T>()` are unaffected — their four return shapes were always part of the method signature.

## 2. Read verbs take query parameters as the second argument

`GetAsync` / `DeleteAsync` / `HeadAsync` / `OptionsAsync` changed their second argument from "request body" to "query parameters"; the parameter is now named `query` and its value goes into `Params` (same flattening and escaping rules as `c.Params`). Write verbs (`PostAsync` / `PutAsync` / `PatchAsync` / `*FormAsync`) are unchanged.

| 1.7 | 1.8 |
| --- | --- |
| `GetAsync(url, new { page = 1 })` → sends a GET body | `GetAsync(url, new { page = 1 })` → `?page=1` |
| `DeleteAsync(url, new { reason = "x" })` → sends a DELETE body | `DeleteAsync(url, new { reason = "x" })` → `?reason=x` |
| — | `GetAsync(url, c => c.Body = new { page = 1 })` (body on a read verb) |
| `DeleteAsync("orders/1", data: null)` (drops the instance default body) | `DeleteAsync("orders/1", c => c.Body = null)` |
| `GetAsync(url, stream)` (sent as a body) | throws `ERR_BAD_OPTION_VALUE`; write `c => c.Body = stream` |

- An `AxiosConfig` / `CancellationToken` in the second position still throws `ERR_BAD_OPTION_VALUE` right away (the read-verb message now says "query parameter").
- Named arguments must follow: `GetAsync(url, data: x)` → `GetAsync(url, query: x)`.

## 3. Body binding: `[RootBody]` is gone, `[Body]` has two shapes

A member's place in the body is no longer spelled by two attributes, nor inferred from how many body sources there are — the shape of `[Body]` is the answer:

| Declaration | Meaning |
| --- | --- |
| `[Body]` | that value is the whole body: sent as-is, no key shell, and it must be the only body source |
| `[Body("name")]` | one key of the aggregated body (`{ "name": value, … }`), repeatable |
| `[Body("")]` / undecorated | the key falls back to `[JsonPropertyName]`, then to the property / parameter name |

| 1.7 | 1.8 |
| --- | --- |
| `[RootBody] x` (1.7.2) / `[Body] x` (1.7.0–1.7.1): the whole body | `[Body] x` |
| `[Body] x` (1.7.2): one key of the aggregated body | `[Body("name")] x`; write `[Body("")] x` to keep the previous key rule |
| `RootBody()` builder | `Body()` (no argument = the whole body), `Body("name")` |
| `BodyAttribute(string name = "")` with a non-null `Name` | `BodyAttribute()` / `BodyAttribute(string name)`, `Name` is `string?` (`null` = the whole body) |

- Upgrading from 1.7.2, a bare `[Body]` needs a name (1.7.2 had already moved the whole-body meaning to `[RootBody]`); upgrading from 1.7.0 / 1.7.1, a bare `[Body]` stays as it is and `[RootBody]` becomes `[Body]`.
- The rule "undecorated members on POST / PUT / PATCH join the body" is unchanged; what changed is that they only aggregate **by key** and no longer mix with a whole body.
- Combining a bare `[Body]` with `[Body("name")]`, with undecorated members on a body method, with another bare `[Body]` or with `[Multipart]` is a build-time error: the source generator reports `AXA017`, and without the generator the binding builder throws `ERR_BAD_OPTION_VALUE` on first use; the message offers two ways out — move the other member to another channel, or turn the whole body into `[Body("name")]`.
- When the whole body is an array / scalar / stream, no subclass is needed any more: put `[Body]` on that member.
- The interface-style sample's composite parameter follows along from `[RootBody]` to `[Body]`, so the body is no longer wrapped in the parameter name (`{ "payload": { … } }` → `{ … }`), matching the model style.
