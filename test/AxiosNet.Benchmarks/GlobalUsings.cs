// 隐式 using 之外额外常用的命名空间（与 src/AxiosNet/GlobalUsings.cs 同一形态）。
global using System.Text;

// Internal 内核按职责拆分命名空间，基准直接引用内部实现量取解析路径成本。
global using AxiosNet.Internal.Content;
global using AxiosNet.Internal.Headers;
global using AxiosNet.Internal.Response;
global using AxiosNet.Internal.Serialization;
global using AxiosNet.Internal.Support;
global using AxiosNet.Internal.Url;
