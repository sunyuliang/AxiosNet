using System.Text.Json;
using AxiosNet.Serialization;

namespace AxiosNet;

// 分组设置独立成文件：AxiosConfig 只有「怎么用」按领域切片，嵌套类型各自放在自己的部分里。

public sealed partial class AxiosConfig
{
    /// <summary>
    /// 序列化相关设置（<see cref="AxiosConfig.Serialization"/>）。
    /// </summary>
    public sealed class SerializationSettings
    {
        /// <summary>
        /// JSON 序列化选项，<b>只对默认 JSON 序列化器生效</b>（请求序列化 / 响应反序列化）。
        /// <para>表单与 multipart 的字段命名与本项无关，见 <see cref="FormFieldNameSelector"/>。</para>
        /// </summary>
        public JsonSerializerOptions? JsonSerializerOptions { get; set; }

        /// <summary>
        /// 本次请求（或本实例默认）使用的内容序列化器，<b>优先级最高</b>：
        /// 高于 <see cref="AxiosContent.Serializer"/>，也高于按媒体类型的自动匹配。
        /// <para>null 表示不覆盖，继续走「全局注入 → 按 Content-Type 自动匹配 → 内置 JSON」。</para>
        /// <para>
        /// 只有<b>业务对象</b>经过它：string / byte[] / Stream / HttpContent 请求体原样发送，
        /// 响应侧 string / byte[] / Stream / 基础类型 / dynamic 由库自己处理。
        /// 响应侧走文本还是字节入口，由响应 <c>Content-Type</c> 判定。
        /// </para>
        /// </summary>
        public IContentSerializer? ContentSerializer { get; set; }

        /// <summary>
        /// 请求体文本编码（string / 对象 / 表单），决定写入 socket 的字节与 Content-Type 的 charset。
        /// null 时使用 UTF-8（axios 的默认行为）。byte[] / Stream 原样透传，不受该项影响。
        /// </summary>
        public Encoding? RequestEncoding { get; set; }

        /// <summary>
        /// 响应体文本编码。null 时自动识别（Content-Type charset → BOM → UTF-8）；
        /// 设置后强制按指定编码解码原始字节（用于服务端 charset 缺失/错误的情况）。
        /// </summary>
        public Encoding? ResponseEncoding { get; set; }

        /// <summary>
        /// 对应 axios paramsSerializer：自定义 query string 生成方式，返回不含前导 <c>?</c> 的查询串。
        /// null 时使用内置规则（数组 <c>a[]=v</c>、嵌套 <c>a[b]=v</c>、null 跳过）。
        /// </summary>
        public Func<object?, string>? ParamsSerializer { get; set; }

        /// <summary>
        /// 表单 / multipart 字段名生成规则：参数为（父级前缀, 键名），返回实际字段名。
        /// 默认 <c>prefix[key]</c>（无前缀即 key）；可改成 <c>prefix.key</c> 或忽略前缀。
        /// <para>
        /// 表单不走序列化器，所以这是字段命名的<b>唯一入口</b>（属性级覆盖用 <c>[JsonPropertyName]</c>）：
        /// 与 <see cref="JsonSerializerOptions"/> / <see cref="ContentSerializer"/> 完全无关，换序列化器不影响字段名。
        /// </para>
        /// </summary>
        public Func<string?, string, string>? FormFieldNameSelector { get; set; }

        private AxiosCollectionFormat _collectionFormat = AxiosCollectionFormat.Brackets;

        /// <summary>
        /// <see cref="AxiosConfig.Params"/> 里集合值的编码形态，默认 <see cref="AxiosCollectionFormat.Brackets"/>（<c>a[]=1&amp;a[]=2</c>）。
        /// <para>
        /// 按请求显式赋值即可覆盖实例默认值；设置了 <see cref="ParamsSerializer"/> 时本项不参与
        /// （自定义序列化器接管整条查询串）。
        /// </para>
        /// </summary>
        public AxiosCollectionFormat CollectionFormat
        {
            get => _collectionFormat;
            set
            {
                _collectionFormat = value;
                CollectionFormatSet = true;
            }
        }

        /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
        internal bool CollectionFormatSet { get; set; }

        /// <summary>
        /// 把 <paramref name="incoming"/> 里「有值」的项覆盖到本实例（合并层内部使用）。
        /// <para>本组的引用类型项以 <c>null</c> 表示未设置，所以直接 <c>??</c> 回退，无需逐项 if。</para>
        /// </summary>
        internal void MergeFrom(SerializationSettings incoming)
        {
            JsonSerializerOptions = incoming.JsonSerializerOptions ?? JsonSerializerOptions;
            ContentSerializer = incoming.ContentSerializer ?? ContentSerializer;
            RequestEncoding = incoming.RequestEncoding ?? RequestEncoding;
            ResponseEncoding = incoming.ResponseEncoding ?? ResponseEncoding;
            ParamsSerializer = incoming.ParamsSerializer ?? ParamsSerializer;
            FormFieldNameSelector = incoming.FormFieldNameSelector ?? FormFieldNameSelector;

            // CollectionFormat 是值类型，只能靠 *Set 标记区分「未设置」与默认值。
            if (incoming.CollectionFormatSet)
            {
                CollectionFormat = incoming.CollectionFormat;
            }
        }

        internal SerializationSettings Clone() => (SerializationSettings)MemberwiseClone();
    }
}
