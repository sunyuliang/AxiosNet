// 隐式 using（Microsoft.NET.Sdk 默认集合之外额外常用的命名空间）
global using System.Net;
global using System.Text;
global using System.Threading;

// Internal 内核按职责拆分为多个命名空间；程序集内各处共用，统一在此导入。
global using AxiosNet.Internal.Content;
global using AxiosNet.Internal.Headers;
global using AxiosNet.Internal.Response;
global using AxiosNet.Internal.Serialization;
global using AxiosNet.Internal.Support;
global using AxiosNet.Internal.Url;
