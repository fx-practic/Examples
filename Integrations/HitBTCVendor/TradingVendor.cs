﻿using HitBTC.Net;
using HitBTC.Net.Communication;
using HitBTC.Net.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Integration;

namespace HitBTCVendor
{
    internal class TradingVendor : MarketDataVendor
    {
        #region Consts
        private const string ACCOUNT = "hitBTC";

        private const int REPORT_ORDERS_HISTORY = 1;
        private const int REPORT_TRADES_HISTORY = 2;
        private const int REPORT_TRANSACTIONS_HISTORY = 3;

        private const int ORDERS_HISTORY_LIMIT = 100;
        private const int TRADES_HISTORY_LIMIT = 100;
        private const int TRANSACTIONS_HISTORY_LIMIT = 100;
        #endregion Consts

        #region Properies
        protected override HitConfig HitConfig => this.hitConfig;
        private HitConfig hitConfig;

        private List<MessageCryptoAssetBalances> balancesCache;
        private Dictionary<string, MessageOpenOrder> ordersCache;

        private Timer timer;

        private CancellationTokenSource updateBalancesCancellation;
        #endregion Properties

        #region Connection

        public override ConnectionResult Connect(ConnectRequestParameters connectRequestParameters)
        {
            var token = connectRequestParameters.CancellationToken;

            // read settings
            string apiKey = null;
            string secret = null;

            var settingItem = connectRequestParameters.ConnectionSettings.GetItemByPath(LOGIN_PARAMETER_GROUP, HitBTCVendor.PARAMETER_API_KEY);
            if (settingItem != null)
                apiKey = settingItem.Value?.ToString();

            if (string.IsNullOrEmpty(apiKey))
                return ConnectionResult.CreateFail("API key is empty");

            settingItem = connectRequestParameters.ConnectionSettings.GetItemByPath(LOGIN_PARAMETER_GROUP, HitBTCVendor.PARAMETER_SECRET_KEY);
            if (settingItem != null && settingItem.Value is PasswordHolder passwordHolder)
                secret = passwordHolder.Password;

            if (string.IsNullOrEmpty(secret))
                return ConnectionResult.CreateFail("Secret key is empty");

            // create config
            this.hitConfig = new HitConfig
            {
                ApiKey = apiKey,
                Secret = secret
            };

            if (token.IsCancellationRequested)
                return ConnectionResult.CreateCancelled();

            // connect
            var result = base.Connect(connectRequestParameters);

            if (result.State != ConnectionState.Connected || result.Cancelled)
                return result;

            // login
            this.CheckHitResponse(this.socketApi.LoginAsync(token).Result, out var error);
            if (error != null)
                return ConnectionResult.CreateFail(error.ToString());

            if (token.IsCancellationRequested)
                return ConnectionResult.CreateCancelled();

            // subscribe reports
            this.CheckHitResponse(this.socketApi.SubscribeReportsAsync(token).Result, out error);
            if (error != null)
                return ConnectionResult.CreateFail(error.ToString());

            if (token.IsCancellationRequested)
                return ConnectionResult.CreateCancelled();

            // get balances
            this.UpdateBalances(token, out error);
            if (error != null)
                return ConnectionResult.CreateFail(error.ToString());

            if (token.IsCancellationRequested)
                return ConnectionResult.CreateCancelled();

            return ConnectionResult.CreateSuccess();
        }

        public override void Disconnect()
        {
            if (this.timer != null)
            {
                this.timer.Change(Timeout.Infinite, Timeout.Infinite);
                this.timer.Dispose();
            }

            base.Disconnect();
        }

        public override void OnConnected(CancellationToken token)
        {
            this.timer = new Timer(this.TimerCallback);
            this.timer.Change(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

            base.OnConnected(token);
        }
        #endregion Connection

        #region Accounts and rules
        public override IList<MessageAccount> GetAccounts() => new List<MessageAccount>
        {
            new MessageCryptoAccount
            {
                AccountId = ACCOUNT,
                AccountName = ACCOUNT,
                AccountAdditionalInfo = new List<AccountAdditionalInfoItem>()
            }
        };

        public override IList<MessageCryptoAssetBalances> GetCryptoAssetBalances()
        {
            var result = new List<MessageCryptoAssetBalances>();

            if (this.balancesCache != null)
                result.AddRange(this.balancesCache);

            return result;
        }

        public override IList<MessageRule> GetRules() => new List<MessageRule>
        {
            new MessageRule
            {
                Name = Rule.ALLOW_SL,
                Value = false
            },
            new MessageRule
            {
                Name = Rule.ALLOW_TP,
                Value = false
            }
        };
        #endregion Accounts and rules

        #region Orders
        public override IList<OrderType> GetAllowedOrderTypes() => new List<OrderType>
        {
            new MarketOrderType(TimeInForce.GTC, TimeInForce.IOC, TimeInForce.FOK, TimeInForce.Day, TimeInForce.GTD),
            new LimitOrderType(TimeInForce.GTC, TimeInForce.IOC, TimeInForce.FOK, TimeInForce.Day, TimeInForce.GTD),
            new StopOrderType(TimeInForce.GTC, TimeInForce.IOC, TimeInForce.FOK, TimeInForce.Day, TimeInForce.GTD),
            new StopLimitOrderType(TimeInForce.GTC, TimeInForce.IOC, TimeInForce.FOK, TimeInForce.Day, TimeInForce.GTD)
        };

        public override IList<MessageOpenOrder> GetPendingOrders()
        {
            var result = new List<MessageOpenOrder>();

            result.AddRange(this.ordersCache.Values);

            return result;
        }
        #endregion Orders

        #region Trading
        public override TradingOperationResult PlaceOrder(PlaceOrderRequestParameters parameters)
        {
            var result = new TradingOperationResult();

            string symbol = parameters.Symbol.Id;
            HitSide side = parameters.Side == Side.Buy ? HitSide.Buy : HitSide.Sell;
            decimal quantity = (decimal)parameters.Quantity;

            decimal price = -1;
            decimal stopPrice = -1;

            if (parameters.OrderTypeId == OrderType.Limit || parameters.OrderTypeId == OrderType.StopLimit)
                price = (decimal)parameters.Price;

            if (parameters.OrderTypeId == OrderType.Stop || parameters.OrderTypeId == OrderType.StopLimit)
                stopPrice = (decimal)parameters.TriggerPrice;

            HitTimeInForce timeInForce = this.ConvertTimeInForce(parameters.TimeInForce);

            DateTime expireTime = default;
            if (timeInForce == HitTimeInForce.GTD)
                expireTime = parameters.ExpirationTime;

            var response = this.CheckHitResponse(this.socketApi.PlaceNewOrderAsync(symbol, side, quantity, price, stopPrice, timeInForce, expireTime, cancellationToken: parameters.CancellationToken).Result, out var error);

            if (response != null)
            {
                result.Status = TradingOperationResultStatus.Success;
                result.OrderId = response.ClientOrderId;
            }
            else
            {
                result.Status = TradingOperationResultStatus.Failure;
                result.Message = error?.ToString() ?? "Unknown error";
            }

            return result;
        }

        public override TradingOperationResult ModifyOrder(ModifyOrderRequestParameters parameters)
        {
            var result = new TradingOperationResult();

            string orderId = parameters.OrderId;
            decimal quantity = (decimal)parameters.Quantity;
            decimal price = -1;

            if (parameters.OrderTypeId == OrderType.Limit || parameters.OrderTypeId == OrderType.StopLimit)
                price = (decimal)parameters.Price;
            else
                price = (decimal)parameters.TriggerPrice;

            var response = this.CheckHitResponse(this.socketApi.ReplaceOrderAsync(orderId, quantity, price, cancellationToken: parameters.CancellationToken).Result, out var error);

            if (response != null)
            {
                result.Status = TradingOperationResultStatus.Success;
                result.OrderId = response.ClientOrderId;
            }
            else
            {
                result.Status = TradingOperationResultStatus.Failure;
                result.Message = error?.ToString() ?? "Unknown error";
            }

            return result;
        }

        public override TradingOperationResult CancelOrder(CancelOrderRequestParameters parameters)
        {
            var result = new TradingOperationResult();

            var response = this.CheckHitResponse(this.socketApi.CancelOrderAsync(parameters.Order.Id, parameters.CancellationToken).Result, out var error);

            if (response != null)
            {
                result.Status = TradingOperationResultStatus.Success;
                result.OrderId = response.ClientOrderId;
            }
            else
            {
                result.Status = TradingOperationResultStatus.Failure;
                result.Message = error?.ToString() ?? "Unknown error";
            }

            return result;
        }
        #endregion Trading

        #region Reports
        public override IList<MessageReportType> GetReportsMetaData() => new List<MessageReportType>
        {
            new MessageReportType
            {
                Id = REPORT_ORDERS_HISTORY,
                Name = "Orders history",
                Parameters = new List<SettingItem>
                {
                    new SettingItemSymbol(REPORT_TYPE_PARAMETER_SYMBOL),
                    new SettingItemDateTime(REPORT_TYPE_PARAMETER_DATETIME_FROM),
                    new SettingItemDateTime(REPORT_TYPE_PARAMETER_DATETIME_TO)
                }
            },
            new MessageReportType
            {
                Id = REPORT_TRADES_HISTORY,
                Name = "Trades history",
                Parameters = new List<SettingItem>
                {
                    new SettingItemSymbol(REPORT_TYPE_PARAMETER_SYMBOL),
                    new SettingItemDateTime(REPORT_TYPE_PARAMETER_DATETIME_FROM),
                    new SettingItemDateTime(REPORT_TYPE_PARAMETER_DATETIME_TO)
                }
            },
            new MessageReportType
            {
                Id = REPORT_TRANSACTIONS_HISTORY,
                Name = "Transactions history",
                Parameters = new List<SettingItem>
                {
                    new SettingItemDateTime(REPORT_TYPE_PARAMETER_DATETIME_FROM),
                    new SettingItemDateTime(REPORT_TYPE_PARAMETER_DATETIME_TO)
                }
            }
        };

        public override Report GenerateReport(ReportRequestParameters reportRequestParameters)
        {
            switch (reportRequestParameters.ReportType.Id)
            {
                case REPORT_ORDERS_HISTORY:
                    return this.GenerateOrdersHistoryReport(reportRequestParameters);
                case REPORT_TRADES_HISTORY:
                    return this.GenerateTradesHistoryReport(reportRequestParameters);
                case REPORT_TRANSACTIONS_HISTORY:
                    return this.GenerateTransactionsReport(reportRequestParameters);
                default:
                    return base.GenerateReport(reportRequestParameters);
            }
            
        }

        private Report GenerateOrdersHistoryReport(ReportRequestParameters reportRequestParameters)
        {
            var report = new Report();

            report.AddColumn("Created at", ComparingType.DateTime);
            report.AddColumn("Symbol", ComparingType.String);
            report.AddColumn("Side", ComparingType.String);
            report.AddColumn("Order type", ComparingType.String);
            report.AddColumn("Quantity", ComparingType.Double);
            report.AddColumn("Cumulative quantity", ComparingType.Double);
            report.AddColumn("Price", ComparingType.Double);
            report.AddColumn("Stop price", ComparingType.Double);
            report.AddColumn("Time in force", ComparingType.String);
            report.AddColumn("Expire at", ComparingType.DateTime);
            report.AddColumn("Status", ComparingType.String);
            report.AddColumn("Updated at", ComparingType.DateTime);
            report.AddColumn("Client order id", ComparingType.String);
            report.AddColumn("Order id", ComparingType.Long);

            string symbolName = null;

            var settingItem = reportRequestParameters.ReportType.Settings.GetItemByName(REPORT_TYPE_PARAMETER_SYMBOL);
            if (settingItem?.Value is Symbol symbol)
                symbolName = symbol.Id;

            if (!this.TryGetReportFromTo(reportRequestParameters.ReportType.Settings, out var from, out var to))
                return report;

            var ordersHistory = new List<HitOrder>();

            while(from < to)
            {
                var orders = this.CheckHitResponse(this.restApi.GetOrdersHistoryAsync(symbolName, from: from, till: to, limit: ORDERS_HISTORY_LIMIT, cancellationToken: reportRequestParameters.CancellationToken).Result, out var error);

                if (orders == null || error != null || orders.Length == 0)
                    break;

                ordersHistory.AddRange(orders);

                to = orders.Last().CreatedAt.AddTicks(-1);

                if (orders.Length < ORDERS_HISTORY_LIMIT)
                    break;
            }

            foreach(var order in ordersHistory)
            {
                var row = new ReportRow();

                row.AddCell(order.CreatedAt, new DateTimeFormatterDescription(order.CreatedAt));
                row.AddCell(order.Symbol);
                row.AddCell(order.Side.ToString());
                row.AddCell(this.ConvertOrderType(order.OrderType));
                row.AddCell(order.Quantity, new VolumeFormattingDescription((double)order.Quantity, order.Symbol));
                row.AddCell(order.CumulativeQuantity, new VolumeFormattingDescription((double)order.CumulativeQuantity, order.Symbol));
                row.AddCell(order.Price, new PriceFormattingDescription((double)order.Price, order.Symbol));
                row.AddCell(order.StopPrice, new PriceFormattingDescription((double)order.StopPrice, order.Symbol));
                row.AddCell(this.ConvertTimeInForce(order.TimeInForce).ToString());
                row.AddCell(order.ExpireTime, new DateTimeFormatterDescription(order.ExpireTime));
                row.AddCell(this.ConvertOrderStatus(order.Status).ToString());
                row.AddCell(order.UpdatedAt, new DateTimeFormatterDescription(order.UpdatedAt));
                row.AddCell(order.ClientOrderId);
                row.AddCell(order.Id);

                report.Rows.Add(row);
            }

            return report;
        }

        private Report GenerateTradesHistoryReport(ReportRequestParameters reportRequestParameters)
        {
            var report = new Report();

            report.AddColumn("Timestamp", ComparingType.DateTime);
            report.AddColumn("Symbol", ComparingType.String);
            report.AddColumn("Side", ComparingType.String);
            report.AddColumn("Quantity", ComparingType.Double);
            report.AddColumn("Price", ComparingType.Double);
            report.AddColumn("Fee", ComparingType.String);
            report.AddColumn("Trade id", ComparingType.Long);
            report.AddColumn("Client order id", ComparingType.String);
            report.AddColumn("Order id", ComparingType.Long);

            string symbolName = null;

            var settingItem = reportRequestParameters.ReportType.Settings.GetItemByName(REPORT_TYPE_PARAMETER_SYMBOL);
            if (settingItem?.Value is Symbol symbol)
                symbolName = symbol.Id;

            if (!this.TryGetReportFromTo(reportRequestParameters.ReportType.Settings, out var from, out var to))
                return report;

            var tradesHistory = new List<HitUserTrade>();

            while (from < to)
            {
                var trades = this.CheckHitResponse(this.restApi.GetUserTradesHistoryByTimestampAsync(symbolName, from: from, till: to, limit: ORDERS_HISTORY_LIMIT, cancellationToken: reportRequestParameters.CancellationToken).Result, out var error);

                if (trades == null || error != null || trades.Length == 0)
                    break;

                tradesHistory.AddRange(trades);

                to = trades.Last().Timestamp.AddTicks(-1);

                if (trades.Length < TRADES_HISTORY_LIMIT)
                    break;
            }

            foreach(var trade in tradesHistory)
            {
                var row = new ReportRow();

                row.AddCell(trade.Timestamp, new DateTimeFormatterDescription(trade.Timestamp));
                row.AddCell(trade.Symbol);
                row.AddCell(trade.Side.ToString());
                row.AddCell((double)trade.Quantity, new VolumeFormattingDescription((double)trade.Quantity, trade.Symbol));
                row.AddCell((double)trade.Price, new PriceFormattingDescription((double)trade.Price, trade.Symbol));
                row.AddCell((double)trade.Fee, new AssetFormattingDescription(this.symbolsCache[trade.Symbol].FeeCurrency, (double)trade.Fee));
                row.AddCell(trade.Id);
                row.AddCell(trade.ClientOrderId);
                row.AddCell(trade.OrderId);

                report.Rows.Add(row);
            }

            return report;
        }

        private Report GenerateTransactionsReport(ReportRequestParameters reportRequestParameters)
        {
            var report = new Report();

            report.AddColumn("Created at", ComparingType.DateTime);
            report.AddColumn("Id", ComparingType.String);
            report.AddColumn("Index", ComparingType.Long);
            report.AddColumn("Currency", ComparingType.String);
            report.AddColumn("Amount", ComparingType.Double);
            report.AddColumn("Fee", ComparingType.Double);
            report.AddColumn("Address", ComparingType.String);
            report.AddColumn("Hash", ComparingType.String);
            report.AddColumn("Status", ComparingType.String);
            report.AddColumn("Type", ComparingType.String);
            report.AddColumn("Updated at", ComparingType.DateTime);

            if (!this.TryGetReportFromTo(reportRequestParameters.ReportType.Settings, out var from, out var to))
                return report;

            var transactionsHistory = new List<HitTransaction>();

            while(from < to)
            {
                var transactions = this.CheckHitResponse(this.restApi.GetTransactionsHistoryByTimestampAsync(from: from, till: to, limit: ORDERS_HISTORY_LIMIT, cancellationToken: reportRequestParameters.CancellationToken).Result, out var error);

                if (transactions == null || error != null || transactions.Length == 0)
                    break;

                transactionsHistory.AddRange(transactions);

                to = transactions.Last().CreatedAt.AddTicks(-1);

                if (transactions.Length < TRANSACTIONS_HISTORY_LIMIT)
                    break;
            }

            foreach(var transaction in transactionsHistory)
            {
                var row = new ReportRow();

                row.AddCell(transaction.CreatedAt, new DateTimeFormatterDescription(transaction.CreatedAt));
                row.AddCell(transaction.Id);
                row.AddCell(transaction.Index);
                row.AddCell(transaction.Currency);
                row.AddCell((double)transaction.Amount);
                row.AddCell((double)transaction.Fee);
                row.AddCell(transaction.Address);
                row.AddCell(transaction.Hash);
                row.AddCell(transaction.Status.ToString());
                row.AddCell(transaction.Type.ToString());
                row.AddCell(transaction.UpdatedAt, new DateTimeFormatterDescription(transaction.UpdatedAt));

                report.Rows.Add(row);
            }

            return report;
        }

        private bool TryGetReportFromTo(IList<SettingItem> settings, out DateTime from, out DateTime to)
        {
            from = default;
            to = default;

            var settingItem = settings.GetItemByName(REPORT_TYPE_PARAMETER_DATETIME_FROM);
            if (settingItem != null)
                from = (DateTime)settingItem.Value;
            else
                return false;

            settingItem = settings.GetItemByName(REPORT_TYPE_PARAMETER_DATETIME_TO);
            if (settingItem != null)
                to = (DateTime)settingItem.Value;
            else
                return false;

            from = Core.Instance.TimeUtils.ConvertFromSelectedTimeZoneToUTC(from);
            to = Core.Instance.TimeUtils.ConvertFromSelectedTimeZoneToUTC(to);

            return true;
        }
        #endregion Reports

        #region Factory
        private MessageOpenOrder CreateOpenOrder(HitOrder hitOrder)
        {
            var message = new MessageOpenOrder(hitOrder.Symbol)
            {
                AccountId = ACCOUNT,
                ExpirationTime = hitOrder.ExpireTime,
                FilledQuantity = (double)hitOrder.CumulativeQuantity,
                LastUpdateTime = hitOrder.UpdatedAt == default ? hitOrder.CreatedAt : hitOrder.UpdatedAt,
                OrderId = hitOrder.ClientOrderId,
                OrderTypeId = this.ConvertOrderType(hitOrder.OrderType),
                Side = hitOrder.Side == HitSide.Buy ? Side.Buy : Side.Sell,
                Status = this.ConvertOrderStatus(hitOrder.Status),
                TimeInForce = this.ConvertTimeInForce(hitOrder.TimeInForce),
                TotalQuantity = (double)hitOrder.Quantity
            };

            if (hitOrder.OrderType == HitOrderType.Limit || hitOrder.OrderType == HitOrderType.StopLimit)
                message.Price = (double)hitOrder.Price;

            if (hitOrder.OrderType == HitOrderType.StopMarket || hitOrder.OrderType == HitOrderType.StopLimit)
                message.TriggerPrice = (double)hitOrder.StopPrice;

            return message;
        }

        private MessageOpenOrder CreateOpenOrder(HitReport hitReport)
        {
            var message = this.CreateOpenOrder(hitReport as HitOrder);

            if (hitReport.ReportType == HitReportType.Replaced)
                message.Status = OrderStatus.Modified;

            return message;
        }

        private MessageCloseOrder CreateCloseOrder(HitReport hitReport) => new MessageCloseOrder
        {
            OrderId = hitReport.ReportType == HitReportType.Replaced ? hitReport.OriginalRequestClientOrderId : hitReport.ClientOrderId
        };

        private MessageOrderHistory CreateOrderHistory(HitReport hitReport)
        {
            var openOrder = this.CreateOpenOrder(hitReport);

            return new MessageOrderHistory(openOrder);
        }

        private MessageTrade CreateTrade(HitReport hitReport) => new MessageTrade
        {
            TradeId = hitReport.TradeId.ToString(),
            SymbolId = hitReport.Symbol,
            AccountId = ACCOUNT,
            DateTime = hitReport.UpdatedAt,
            Fee = new PnLItem
            {
                AssetID = this.symbolsCache[hitReport.Symbol].FeeCurrency,
                Value = (double)hitReport.TradeFee,
            },
            OrderId = hitReport.ClientOrderId,
            OrderTypeId = this.ConvertOrderType(hitReport.OrderType),
            Price = (double)hitReport.TradePrice,
            Quantity = (double)hitReport.TradeQuantity,
            Side = hitReport.Side == HitSide.Buy ? Side.Buy : Side.Sell
        };
        #endregion Factory

        #region Convertion
        private string ConvertOrderType(HitOrderType hitOrderType)
        {
            switch(hitOrderType)
            {
                case HitOrderType.Market:
                default:
                    return OrderType.Market;
                case HitOrderType.Limit:
                    return OrderType.Limit;
                case HitOrderType.StopMarket:
                    return OrderType.Stop;
                case HitOrderType.StopLimit:
                    return OrderType.StopLimit;
            }
        }

        private OrderStatus ConvertOrderStatus(HitOrderStatus hitOrderStatus)
        {
            switch(hitOrderStatus)
            {
                case HitOrderStatus.New:
                default:
                    return OrderStatus.Opened;
                case HitOrderStatus.Suspended:
                    return OrderStatus.Modified;
                case HitOrderStatus.Canceled:
                case HitOrderStatus.Expired:
                    return OrderStatus.Canceled;
                case HitOrderStatus.PartiallyFilled:
                    return OrderStatus.PartiallyFilled;
                case HitOrderStatus.Filled:
                    return OrderStatus.Filled;
            }
        }

        private TimeInForce ConvertTimeInForce(HitTimeInForce hitTimeInForce)
        {
            switch(hitTimeInForce)
            {
                case HitTimeInForce.Day:
                default:
                    return TimeInForce.Day;
                case HitTimeInForce.GTC:
                    return TimeInForce.GTC;
                case HitTimeInForce.IOC:
                    return TimeInForce.IOC;
                case HitTimeInForce.FOK:
                    return TimeInForce.FOK;
                case HitTimeInForce.GTD:
                    return TimeInForce.GTD;
            }
        }

        private HitTimeInForce ConvertTimeInForce(TimeInForce timeInForce)
        {
            switch(timeInForce)
            {
                case TimeInForce.Day:
                default:
                    return HitTimeInForce.Day;
                case TimeInForce.GTC:
                    return HitTimeInForce.GTC;
                case TimeInForce.IOC:
                    return HitTimeInForce.IOC;
                case TimeInForce.FOK:
                    return HitTimeInForce.FOK;
                case TimeInForce.GTD:
                    return HitTimeInForce.GTD;
            }
        }
        #endregion Convertion

        #region Misc
        private void UpdateBalances(CancellationToken token, out HitError hitError)
        {
            // Trading balances
            var tradingBalances = this.CheckHitResponse(this.socketApi.GetTradingBalanceAsync().Result, out hitError)?
                .Where(b => b.Available + b.Reserved > 0);

            if (tradingBalances == null || hitError != null || token.IsCancellationRequested)
                return;

            // Account balances
            var accountBalances = this.CheckHitResponse(this.restApi.GetAccountBalancesAsync().Result, out hitError)?
                .Where(b => b.Available + b.Reserved > 0);

            if (accountBalances == null || hitError != null || token.IsCancellationRequested)
                return;

            // Tickers
            var tickers = this.CheckHitResponse(this.restApi.GetTickersAsync().Result, out hitError)?
                .ToDictionary(t => t.Symbol);

            if (tickers == null || hitError != null || token.IsCancellationRequested)
                return;

            var totalBalances = new Dictionary<string, (decimal total, decimal available, decimal reserved)>();

            foreach(var balance in tradingBalances)
            {
                if (token.IsCancellationRequested)
                    return;

                if (totalBalances.TryGetValue(balance.Currency, out var value))
                    totalBalances[balance.Currency] = (value.total + balance.Available + balance.Reserved, value.available + balance.Available, value.reserved + balance.Reserved);
                else
                    totalBalances[balance.Currency] = (balance.Available + balance.Reserved, balance.Available, balance.Reserved);
            }

            foreach(var balance in accountBalances)
            {
                if (token.IsCancellationRequested)
                    return;

                if (totalBalances.TryGetValue(balance.Currency, out var value))
                    totalBalances[balance.Currency] = (value.total + balance.Available + balance.Reserved, value.available, value.reserved + balance.Reserved);
                else
                    totalBalances[balance.Currency] = (balance.Available + balance.Reserved, 0, balance.Reserved);
            }

            var messages = new List<MessageCryptoAssetBalances>();

            foreach(var item in totalBalances)
            {
                if (token.IsCancellationRequested)
                    return;

                string currency = item.Key;
                decimal totalBalance = item.Value.total;
                decimal availableBalance = item.Value.available;
                decimal reservedBalance = item.Value.reserved;               

                decimal estimatedBTC = 0;
                decimal estimatedUSD = 0;

                if (currency == "BTC")
                    estimatedBTC = totalBalance;
                else
                {
                    if (tickers.TryGetValue($"{currency}BTC", out var ticker))
                    {
                        if (ticker.Last.HasValue)
                            estimatedBTC = totalBalance * ticker.Last.Value;
                    }
                    else if (tickers.TryGetValue($"BTC{currency}", out ticker))
                    {
                        if (ticker.Last.HasValue)
                            estimatedBTC = totalBalance / ticker.Last.Value;
                    }
                }

                if (currency == "USD")
                    estimatedUSD = totalBalance;
                else
                {
                    if (tickers.TryGetValue($"{currency}USDT", out var ticker))
                    {
                        if (ticker.Last.HasValue)
                            estimatedUSD = totalBalance * ticker.Last.Value;
                    }
                    else if (tickers.TryGetValue($"USDT{currency}", out ticker))
                    {
                        if (ticker.Last.HasValue)
                            estimatedUSD = totalBalance / ticker.Last.Value;
                    }
                    else if (tickers.TryGetValue($"{currency}USD", out ticker))
                    {
                        if (ticker.Last.HasValue)
                            estimatedUSD = totalBalance * ticker.Last.Value;
                    }
                    else if (tickers.TryGetValue($"USD{currency}", out ticker))
                    {
                        if (ticker.Last.HasValue)
                            estimatedUSD = totalBalance / ticker.Last.Value;
                    }
                }

                messages.Add(new MessageCryptoAssetBalances
                {
                    AccountId = ACCOUNT,
                    AssetId = currency,
                    AvailableBalance = (double)availableBalance,
                    ReservedBalance = (double)reservedBalance,
                    TotalBalance = (double)totalBalance,
                    TotalInBTC = (double)estimatedBTC,
                    TotalInUSD = (double)estimatedUSD
                });
            }

            this.balancesCache = messages;
        }

        private async void UpdateBalancesAsync()
        {
            if (this.updateBalancesCancellation != null)
                this.updateBalancesCancellation.Cancel();

            this.updateBalancesCancellation = new CancellationTokenSource();

            HitError error = null;

            await Task.Factory.StartNew(() => this.UpdateBalances(this.updateBalancesCancellation.Token, out error))
                .ContinueWith((t) => 
                {
                    if (error == null)
                        this.balancesCache.ForEach(b => this.PushMessage(b));
                    else
                    {
                        var dealTicket = DealTicketGenerator.CreateRefuseDealTicket(error.ToString());

                        this.PushMessage(dealTicket);
                    }

                    this.updateBalancesCancellation = null;
                });


        }
        #endregion

        protected override void ProcessSocketNotification(HitEventArgs e)
        {
            try
            {
                switch (e.NotificationMethod)
                {
                    case HitNotificationMethod.ActiveOrders:
                        this.ordersCache = new Dictionary<string, MessageOpenOrder>();

                        foreach (var order in e.ActiveOrders)
                        {
                            var message = this.CreateOpenOrder(order);

                            this.ordersCache.Add(message.OrderId, message);
                        }
                        break;
                    case HitNotificationMethod.Report:
                        var messages = new List<Message>();

                        MessageOpenOrder openOrder = null;
                        MessageOrderHistory orderHistory = null;

                        var reportType = e.Report.ReportType;

                        if (reportType == HitReportType.New || reportType == HitReportType.Replaced || reportType == HitReportType.Suspended)
                        {
                            messages.Add(openOrder = this.CreateOpenOrder(e.Report));
                            messages.Add(orderHistory = new MessageOrderHistory(openOrder));
                            messages.Add(DealTicketGenerator.CreateTradingDealTicket(orderHistory, HitBTCVendor.VENDOR_NAME));

                            if (reportType == HitReportType.Replaced)
                            {
                                messages.Add(this.CreateCloseOrder(e.Report));
                                this.ordersCache.Remove(e.Report.OriginalRequestClientOrderId);
                            }

                            this.ordersCache[e.Report.ClientOrderId] = openOrder;
                        }
                        else
                        {
                            messages.Add(this.CreateCloseOrder(e.Report));
                            messages.Add(orderHistory = this.CreateOrderHistory(e.Report));
                            messages.Add(DealTicketGenerator.CreateTradingDealTicket(orderHistory, HitBTCVendor.VENDOR_NAME));

                            this.ordersCache.Remove(e.Report.ClientOrderId);

                            if (reportType == HitReportType.Trade)
                                messages.Add(this.CreateTrade(e.Report));
                        }

                        messages.ForEach(m => this.PushMessage(m));

                        this.UpdateBalancesAsync();
                        break;
                    default:
                        base.ProcessSocketNotification(e);
                        break;
                }
            }
            catch(Exception ex)
            {
                Core.Instance.Loggers.Log(ex);
            }
        }

        private void TimerCallback(object state)
        {
            try
            {
                this.UpdateBalancesAsync();
            }
            catch(Exception ex)
            {
                Core.Instance.Loggers.Log(ex);
            }
        }
    }
}
