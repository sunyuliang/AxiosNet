# AxiosNet 1.8 breaking changes

This is the **1.7 → 1.8** migration reference for callers upgrading from `AxiosNet` 1.7.x to 1.8.x; it matches the public surface shipped since 1.8.0.

Both changes remove a layer rather than add one: declarative calls deliver the body by default, and a shortcut's second argument is now decided by the verb alone.

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

## 3. Samples and docs

Interface-style samples now mark their composite parameter with `[RootBody]`, so the body is no longer wrapped in the parameter name (`{ "payload": { … } }` → `{ … }`), matching the model style. This is a sample-level alignment only: the binding rule is unchanged — undecorated members on POST / PUT / PATCH still aggregate into one object body keyed by member name.
