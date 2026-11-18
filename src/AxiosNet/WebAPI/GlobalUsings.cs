// WebAPI 扩展可被 AxiosNetOmitWebApi 整体排除：本文件位于 WebAPI/ 下，会随目录一起移出编译，
// 因此这里的全局导入不会影响只含内核的构建。
global using AxiosNet.WebAPI.Internal.Authorization;
global using AxiosNet.WebAPI.Internal.Binding;
global using AxiosNet.WebAPI.Internal.Metadata;
global using AxiosNet.WebAPI.Internal.Runtime;
