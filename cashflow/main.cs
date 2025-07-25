using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Generic.PaymentGateway.Offline
{
    /// <summary>
    /// 去識別化版本，請自行替換 API 路徑、Header Key、DTO。
    /// </summary>
    public class GenericPaymentOfflineService : IGenericPaymentOffline
    {
        #region API Paths (placeholder)
        private const string API_CREATE_ORDER  = "/v2/payments/oneTimeKeys/pay";
        private const string API_CHECK_ORDER   = "/v2/payments/orders/{orderId}/check";
        private const string API_QUERY_ORDER   = "/v2/payments";
        private const string API_REFUND_ORDER  = "/v2/payments/orders/{orderId}/refund";
        #endregion

        private readonly IPaymentDbContext _db;
        private readonly ILogger _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        public GenericPaymentOfflineService(
            IPaymentDbContext db,
            ILogger<GenericPaymentOfflineService> logger,
            IHttpClientFactory httpClientFactory)
        {
            _db = db;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }

        /// <summary>
        /// 建立訂單：本地預寫 → 呼叫金流 → 依回傳碼更新。
        /// </summary>
        public async Task<BaseResponse> CreateOrderAsync(
            PaymentOrder order,
            List<PaymentOrderItem> items,
            string oneTimeToken,
            string providerConfigJson,
            CancellationToken ct = default)
        {
            var resp = new BaseResponse();

            // Step 1: 先在本地建立金流商主檔
            var providerRec = new ProviderRecord
            {
                OrderNo       = order.OrderNo,
                InsertTime    = DateTime.UtcNow,
                Amount        = order.Amount,
                PayState      = (byte)PaymentState.Pending, // 0
                ReturnCode    = string.Empty,
                ReturnMessage = string.Empty,
                OrderStatus   = string.Empty,
                ExtraToken    = oneTimeToken
            };

            await _db.ProviderRecords.AddAsync(providerRec, ct);
            await _db.SaveChangesAsync(ct);

            // Step 2: 組 Request
            var env = JsonSerializer.Deserialize<ProviderEnv>(providerConfigJson)!;
            var fullUrl = env.BaseUrl + API_CREATE_ORDER;

            var rqBodyObj = new
            {
                amount      = (int)order.Amount,
                capture     = true,
                currency    = env.Currency ?? "TWD",
                oneTimeKey  = oneTimeToken,
                orderId     = order.OrderNo,
                productName = string.Join(",", items.Select(i => i.ProductName))
            };
            var httpReq = GenerateRequestSkeleton(HttpMethod.Post, fullUrl, env);
            httpReq.Content = new StringContent(JsonSerializer.Serialize(rqBodyObj), Encoding.UTF8, "application/json");

            // Step 3: 送出請求並解析
            var httpClient = _httpClientFactory.CreateClient(nameof(GenericPaymentOfflineService));
            var (ok, status, rawBody) = await SendRequestAsync(httpClient, httpReq, ct);

            if (!ok || string.IsNullOrWhiteSpace(rawBody))
            {
                _logger.LogWarning("Order {OrderNo} create failed/no response. Status={Status} Body={Body}",
                    order.OrderNo, status, rawBody);
                resp.SetCode("1299"); // 自訂：第三方無回應
                return resp;
            }

            var dto = DeserializeSafe<CreateOrderResponse>(rawBody);
            if (dto == null)
            {
                _logger.LogWarning("Order {OrderNo} JSON parse failed: {Body}", order.OrderNo, rawBody);
                resp.SetCode("1299");
                return resp;
            }

            await using var tx = await _db.BeginTransactionAsync(ct);
            var currentOrderState   = (byte)order.StateCode;
            var currentProviderState = providerRec.PayState;

            switch (dto.ReturnCode)
            {
                case "0000":
                    if (dto.Info == null)
                    {
                        _logger.LogWarning("Order {OrderNo} return 0000 but Info null.", order.OrderNo);
                        resp.SetCode("1299");
                        return resp;
                    }

                    currentOrderState   = (byte)PaymentState.Paid;   // 1
                    currentProviderState = (byte)PaymentState.Paid;  // 1

                    var officialTime = TryParseDate(dto.Info.TransactionDate) ?? DateTime.UtcNow;

                    providerRec.OrderStatus   = "COMPLETE";
                    providerRec.InsertTime    = officialTime;
                    providerRec.ReturnCode    = dto.ReturnCode;
                    providerRec.ReturnMessage = dto.ReturnMessage;
                    providerRec.TransactionId = dto.Info.TransactionId;

                    // 寫入明細與支付資訊
                    var payDetail = new ProviderDetail
                    {
                        OrderNo         = order.OrderNo,
                        Amount          = order.Amount,
                        AmountWithoutFee= order.Amount,
                        TransactionDate = officialTime,
                        TransactionId   = dto.Info.TransactionId,
                        Type            = "PAYMENT"
                    };

                    await _db.ProviderDetails.AddAsync(payDetail, ct);

                    if (dto.Info.PayInfo != null)
                    {
                        var payInfos = dto.Info.PayInfo.Select(x => new ProviderPayInfo
                        {
                            OrderNo     = order.OrderNo,
                            Amount      = x.Amount,
                            Method      = x.Method,
                            MaskedCard  = x.MaskedCreditCardNumber
                        }).ToList();

                        await _db.ProviderPayInfos.AddRangeAsync(payInfos, ct);
                    }
                    break;

                case "1165": // 等待/未確認
                    currentOrderState   = (byte)PaymentState.Pending; // 0
                    currentProviderState = (byte)PaymentState.Pending; // 0
                    resp.SetCode("1201");
                    break;

                default:
                    _logger.LogWarning("Order {OrderNo} create failed. Code={Code}, Msg={Msg}",
                        order.OrderNo, dto.ReturnCode, dto.ReturnMessage);
                    currentOrderState   = (byte)PaymentState.Failed; // 2
                    currentProviderState = (byte)PaymentState.Failed; // 2
                    resp.SetCode("1202");
                    break;
            }

            // 寫回狀態
            order.StateCode           = currentOrderState;
            providerRec.PayState      = currentProviderState;
            providerRec.ReturnCode    = dto.ReturnCode;
            providerRec.ReturnMessage = dto.ReturnMessage;

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return resp;
        }

        /// <summary>
        /// 退款。
        /// </summary>
        public async Task<BaseResponse> RefundOrderAsync(
            RefundRequest rq,
            PaymentOrder order,
            string providerConfigJson,
            CancellationToken ct = default)
        {
            var resp = new BaseResponse();

            // 1. 檢查主檔是否存在
            var record = await _db.ProviderRecords
                .FirstOrDefaultAsync(x => x.OrderNo == rq.OrderNo, ct);

            if (record == null)
            {
                resp.SetCode("2101"); // 無金流主檔
                return resp;
            }

            // 2. 檢查退款金額是否合法
            if (rq.RefundAmount + record.RefundAmount > record.Amount)
            {
                resp.SetCode("2102"); // 退款超額
                return resp;
            }

            // 記錄原狀態並壓成退款進行中
            var prevOrderState    = order.StateCode;
            var prevProviderState = record.PayState;

            order.StateCode   = (byte)PaymentState.Refunding; // 4
            record.PayState   = (byte)PaymentState.Refunding; // 4
            await _db.SaveChangesAsync(ct);

            // 3. Call API
            var env = JsonSerializer.Deserialize<ProviderEnv>(providerConfigJson)!;
            var apiPath  = API_REFUND_ORDER.Replace("{orderId}", rq.OrderNo);
            var fullUrl  = $"{env.BaseUrl}{apiPath}";

            var rqBodyObj = new { refundAmount = (int)rq.RefundAmount };
            var httpReq   = GenerateRequestSkeleton(HttpMethod.Post, fullUrl, env);
            httpReq.Content = new StringContent(JsonSerializer.Serialize(rqBodyObj), Encoding.UTF8, "application/json");

            var httpClient = _httpClientFactory.CreateClient(nameof(GenericPaymentOfflineService));
            var (ok, status, rawBody) = await SendRequestAsync(httpClient, httpReq, ct);

            if (!ok || string.IsNullOrWhiteSpace(rawBody))
            {
                _logger.LogWarning("Order {OrderNo} refund failed/no response. Status={Status} Body={Body}",
                    order.OrderNo, status, rawBody);
                resp.SetCode("2299"); // 第三方無回應
                return resp;
            }

            var dto = DeserializeSafe<RefundResponse>(rawBody);
            if (dto == null)
            {
                _logger.LogWarning("Order {OrderNo} refund JSON parse failed: {Body}", order.OrderNo, rawBody);
                resp.SetCode("2299");
                return resp;
            }

            await using var tx = await _db.BeginTransactionAsync(ct);

            switch (dto.ReturnCode)
            {
                case "0000":
                    if (dto.Info == null)
                    {
                        _logger.LogWarning("Order {OrderNo} refund return 0000 but Info null.", order.OrderNo);
                        resp.SetCode("2299");
                        return resp;
                    }

                    order.StateCode   = (byte)PaymentState.Refunded; // 5
                    record.PayState   = (byte)PaymentState.Refunded; // 5
                    record.RefundAmount += rq.RefundAmount;

                    await _db.ProviderDetails.AddAsync(new ProviderDetail
                    {
                        OrderNo         = order.OrderNo,
                        Amount          = rq.RefundAmount,
                        TransactionDate = TryParseDate(dto.Info.RefundTransactionDate) ?? DateTime.UtcNow,
                        TransactionId   = dto.Info.RefundTransactionId,
                        Type            = "REFUND"
                    }, ct);
                    break;

                case "1164": // 與金流商不同步，需要人工介入
                    _logger.LogWarning("Order {OrderNo} refund mismatch with provider. Code={Code}, Msg={Msg}",
                        order.OrderNo, dto.ReturnCode, dto.ReturnMessage);
                    resp.SetCode("2201");

                    order.StateCode = (byte)PaymentState.Manual; // 6
                    record.PayState = (byte)PaymentState.Manual; // 6
                    break;

                default: // 退款失敗 → 人工處理
                    _logger.LogWarning("Order {OrderNo} refund failed. Code={Code}, Msg={Msg}",
                        order.OrderNo, dto.ReturnCode, dto.ReturnMessage);
                    resp.SetCode("2201");

                    order.StateCode = (byte)PaymentState.Manual; // 6
                    record.PayState = (byte)PaymentState.Manual; // 6
                    break;
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return resp;
        }

        /// <summary>
        /// 查詢訂單狀態/補資料。
        /// </summary>
        public async Task<QueryResponse> QueryOrderAsync(
            PaymentOrder order,
            string providerConfigJson,
            CancellationToken ct = default)
        {
            var result = new QueryResponse();

            var record = await _db.ProviderRecords.FirstAsync(x => x.OrderNo == order.OrderNo, ct);
            var refundAmountBefore = record.RefundAmount;

            var currentOrderState   = (byte)order.StateCode;
            var currentProviderState = record.PayState;

            var env = JsonSerializer.Deserialize<ProviderEnv>(providerConfigJson)!;
            var fullUrl = $"{env.BaseUrl}{API_QUERY_ORDER}?orderId={order.OrderNo}";
            var httpReq = GenerateRequestSkeleton(HttpMethod.Get, fullUrl, env);

            var httpClient = _httpClientFactory.CreateClient(nameof(GenericPaymentOfflineService));
            var (ok, status, rawBody) = await SendRequestAsync(httpClient, httpReq, ct);

            if (!ok || string.IsNullOrWhiteSpace(rawBody))
            {
                _logger.LogWarning("Order {OrderNo} query failed/no response. Status={Status} Body={Body}",
                    order.OrderNo, status, rawBody);
                result.RefundAmount = refundAmountBefore;
                return result;
            }

            var dto = DeserializeSafe<QueryOrderResponse>(rawBody);
            if (dto == null)
            {
                _logger.LogWarning("Order {OrderNo} query JSON parse failed: {Body}", order.OrderNo, rawBody);
                result.RefundAmount = refundAmountBefore;
                return result;
            }

            await using var tx = await _db.BeginTransactionAsync(ct);

            switch (dto.ReturnCode)
            {
                case "0000":
                    if (dto.Info == null || !dto.Info.Any())
                    {
                        _logger.LogWarning("Order {OrderNo} query 0000 but Info null/empty.", order.OrderNo);
                        break;
                    }

                    var currentInfo = dto.Info.Last(); // 以最後一筆為最新
                    // 補退款明細
                    if (currentInfo.RefundList != null && currentInfo.RefundList.Any())
                    {
                        var refundItems = currentInfo.RefundList;

                        var existingIds = await _db.ProviderDetails
                            .Where(x => x.OrderNo == order.OrderNo)
                            .Select(x => x.TransactionId)
                            .ToListAsync(ct);

                        var newRefundDetails = refundItems
                            .Where(r => !existingIds.Contains(r.RefundTransactionId))
                            .Select(r => new ProviderDetail
                            {
                                OrderNo         = order.OrderNo,
                                TransactionId   = r.RefundTransactionId,
                                TransactionType = "REFUND",
                                TransactionDate = TryParseDate(r.RefundTransactionDate) ?? DateTime.UtcNow,
                                // 查詢補資料：退款金額使用正數 (或 Math.Abs)
                                Amount = Math.Abs(r.RefundAmount)
                            })
                            .ToList();

                        if (newRefundDetails.Count > 0)
                        {
                            record.RefundAmount += newRefundDetails.Sum(d => d.Amount);
                            await _db.ProviderDetails.AddRangeAsync(newRefundDetails, ct);

                            currentOrderState   = (byte)PaymentState.Refunded; // 5
                            currentProviderState = (byte)PaymentState.Refunded; // 5
                        }
                    }
                    else
                    {
                        // 若本地仍為 Pending，但金流商已付款 → 補子表＆更新成功狀態
                        if (record.PayState == (byte)PaymentState.Pending)
                        {
                            var officalDate = TryParseDate(currentInfo.TransactionDate) ?? DateTime.UtcNow;

                            var payDetail = new ProviderDetail
                            {
                                OrderNo         = order.OrderNo,
                                Amount          = order.Amount,
                                AmountWithoutFee= order.Amount,
                                TransactionDate = officalDate,
                                TransactionId   = currentInfo.TransactionId,
                                TransactionType = "PAYMENT"
                            };

                            await _db.ProviderDetails.AddAsync(payDetail, ct);

                            if (currentInfo.PayInfo != null)
                            {
                                var payInfos = currentInfo.PayInfo.Select(x => new ProviderPayInfo
                                {
                                    OrderNo = order.OrderNo,
                                    Amount  = x.Amount,
                                    Method  = x.Method
                                }).ToList();

                                await _db.ProviderPayInfos.AddRangeAsync(payInfos, ct);
                            }

                            record.OrderStatus   = "COMPLETE";
                            record.InsertTime    = officalDate;
                            record.ReturnCode    = dto.ReturnCode;
                            record.ReturnMessage = dto.ReturnMessage;
                            record.TransactionId = currentInfo.TransactionId;

                            currentOrderState   = (byte)PaymentState.Paid; // 1
                            currentProviderState = (byte)PaymentState.Paid; // 1
                        }
                    }

                    result.RefundAmount = record.RefundAmount;
                    break;

                case "1150": // 查無交易紀錄
                    _logger.LogWarning("Order {OrderNo} query not found. Code={Code}, Msg={Msg}",
                        order.OrderNo, dto.ReturnCode, dto.ReturnMessage);
                    currentOrderState   = (byte)PaymentState.Failed; // 2
                    currentProviderState = (byte)PaymentState.Failed; // 2
                    result.RefundAmount = refundAmountBefore;
                    break;

                default:
                    _logger.LogWarning("Order {OrderNo} query failed. Code={Code}, Msg={Msg}",
                        order.OrderNo, dto.ReturnCode, dto.ReturnMessage);
                    result.SetCode("3202");
                    // 狀態不變
                    break;
            }

            order.StateCode      = currentOrderState;
            record.PayState      = currentProviderState;
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return result;
        }

        /// <summary>
        /// 建立基本 HttpRequestMessage，加入必要 Header。
        /// </summary>
        private static HttpRequestMessage GenerateRequestSkeleton(HttpMethod method, string fullUrl, ProviderEnv env)
        {
            var req = new HttpRequestMessage(method, fullUrl);
            req.Headers.TryAddWithoutValidation("X-Provider-Id", env.ChannelId);
            req.Headers.TryAddWithoutValidation("X-Provider-Secret", env.ChannelSecret);
            return req;
        }

        /// <summary>
        /// 真正送出 HTTP 請求 + 讀取字串回應，純原生 HttpClient。
        /// </summary>
        private static async Task<(bool ok, HttpStatusCode status, string body)> SendRequestAsync(
            HttpClient client,
            HttpRequestMessage req,
            CancellationToken ct)
        {
            try
            {
                using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                var body = await res.Content.ReadAsStringAsync(ct);

                if (!res.IsSuccessStatusCode)
                {
                    return (false, res.StatusCode, body);
                }

                return (true, res.StatusCode, body);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                return (false, 0, "timeout");
            }
            catch (Exception ex)
            {
                return (false, 0, ex.Message);
            }
        }

        private static T? DeserializeSafe<T>(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(json);
            }
            catch
            {
                return default;
            }
        }

        private static DateTime? TryParseDate(string? s)
        {
            if (DateTime.TryParse(s, out var dt)) return dt;
            return null;
        }
    }

    #region Interfaces & Enums & DTO (Placeholders / Symbols only)

    public interface IGenericPaymentOffline
    {
        Task<BaseResponse> CreateOrderAsync(PaymentOrder order, List<PaymentOrderItem> items, string oneTimeToken, string providerConfigJson, CancellationToken ct = default);
        Task<BaseResponse> RefundOrderAsync(RefundRequest rq, PaymentOrder order, string providerConfigJson, CancellationToken ct = default);
        Task<QueryResponse> QueryOrderAsync(PaymentOrder order, string providerConfigJson, CancellationToken ct = default);
    }

    public interface IPaymentDbContext
    {
        DbSet<ProviderRecord>     ProviderRecords   { get; }
        DbSet<ProviderDetail>     ProviderDetails   { get; }
        DbSet<ProviderPayInfo>    ProviderPayInfos  { get; }

        Task<int> SaveChangesAsync(CancellationToken ct = default);
        Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default);
    }

    public interface IDbContextTransaction : IAsyncDisposable
    {
        Task CommitAsync(CancellationToken ct = default);
        Task RollbackAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// 代表本地系統使用的狀態碼。數值可自由替換，但此處保留象徵性。
    /// </summary>
    public enum PaymentState : byte
    {
        Pending  = 0,
        Paid     = 1,
        Failed   = 2,
        Refunding= 4,
        Refunded = 5,
        Manual   = 6
    }

    #endregion

    #region Domain Models (Minimal placeholders)

    public class PaymentOrder
    {
        public string OrderNo   { get; set; } = default!;
        public decimal Amount   { get; set; }
        public byte StateCode   { get; set; } // 映射 PaymentState
    }

    public class PaymentOrderItem
    {
        public string ProductName { get; set; } = default!;
        public decimal Amount     { get; set; }
    }

    public class RefundRequest
    {
        public string OrderNo      { get; set; } = default!;
        public decimal RefundAmount { get; set; }
    }

    public class ProviderRecord
    {
        public int    Id             { get; set; }
        public string OrderNo        { get; set; } = default!;
        public string? TransactionId { get; set; }
        public string OrderStatus    { get; set; } = string.Empty;
        public byte   PayState       { get; set; }
        public decimal Amount        { get; set; }
        public decimal RefundAmount  { get; set; }
        public DateTime InsertTime   { get; set; }
        public string ReturnCode     { get; set; } = string.Empty;
        public string ReturnMessage  { get; set; } = string.Empty;
        public string ExtraToken     { get; set; } = string.Empty;
    }

    public class ProviderDetail
    {
        public int      Id               { get; set; }
        public string   OrderNo          { get; set; } = default!;
        public string?  TransactionId    { get; set; }
        public string   TransactionType  { get; set; } = default!;
        public DateTime TransactionDate  { get; set; }
        public decimal  Amount           { get; set; }
        public decimal? AmountWithoutFee { get; set; }
        public string   Type             { get; set; } = default!;
    }

    public class ProviderPayInfo
    {
        public int     Id        { get; set; }
        public string  OrderNo   { get; set; } = default!;
        public string? MaskedCard{ get; set; }
        public decimal Amount    { get; set; }
        public string  Method    { get; set; } = default!;
    }

    #endregion

    #region Responses (DTO from provider, stripped to essentials)

    public class CreateOrderResponse
    {
        public string ReturnCode    { get; set; } = default!;
        public string ReturnMessage { get; set; } = default!;
        public CreateOrderInfo? Info { get; set; }
    }

    public class CreateOrderInfo
    {
        public string TransactionId   { get; set; } = default!;
        public string TransactionDate { get; set; } = default!;
        public List<PayInfoItem>? PayInfo { get; set; }
    }

    public class PayInfoItem
    {
        public string Method                 { get; set; } = default!;
        public decimal Amount                { get; set; }
        public string? MaskedCreditCardNumber{ get; set; }
    }

    public class RefundResponse
    {
        public string ReturnCode    { get; set; } = default!;
        public string ReturnMessage { get; set; } = default!;
        public RefundInfo? Info     { get; set; }
    }

    public class RefundInfo
    {
        public string RefundTransactionId   { get; set; } = default!;
        public string RefundTransactionDate { get; set; } = default!;
    }

    public class QueryOrderResponse
    {
        public string ReturnCode    { get; set; } = default!;
        public string ReturnMessage { get; set; } = default!;
        public List<QueryOrderInfo>? Info { get; set; }
    }

    public class QueryOrderInfo
    {
        public string TransactionId   { get; set; } = default!;
        public string TransactionDate { get; set; } = default!;
        public List<PayInfoItem>? PayInfo { get; set; }
        public List<RefundItem>? RefundList { get; set; }
    }

    public class RefundItem
    {
        public string  RefundTransactionId   { get; set; } = default!;
        public string  RefundTransactionDate { get; set; } = default!;
        public decimal RefundAmount          { get; set; }
    }

    #endregion

    #region Base Response

    public class BaseResponse
    {
        public string Code    { get; private set; } = "0000";
        public string Message { get; private set; } = "OK";

        public void SetCode(string code, string? message = null)
        {
            Code = code;
            Message = message ?? Message;
        }
    }

    public class QueryResponse : BaseResponse
    {
        public decimal RefundAmount { get; set; }
    }

    #endregion

    #region Provider Env (Config)

    public class ProviderEnv
    {
        public string BaseUrl       { get; set; } = default!;
        public string ChannelId     { get; set; } = default!;
        public string ChannelSecret { get; set; } = default!;
        public string? Currency     { get; set; }
    }

    #endregion
}
