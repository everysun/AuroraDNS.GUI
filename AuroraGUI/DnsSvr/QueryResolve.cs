﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.Caching;
using System.Threading.Tasks;
using ARSoft.Tools.Net;
using ARSoft.Tools.Net.Dns;
using AuroraGUI.Tools;
using MojoUnity;
using OhMyDnsPackage;
using static AuroraGUI.Tools.MyTools;

// ReSharper disable CollectionNeverUpdated.Global
#pragma warning disable 649
#pragma warning disable 1998

namespace AuroraGUI.DnsSvr
{
    static class QueryResolve
    {
        public static async Task ServerOnQueryReceived(object sender, QueryReceivedEventArgs e)
        {
            if (!(e.Query is DnsMessage query))
                return;

            IPAddress clientAddress = e.RemoteEndpoint.Address;
            if (DnsSettings.EDnsCustomize)
                clientAddress = Equals(DnsSettings.EDnsIp, IPAddress.Parse("0.0.0.1")) 
                    ? IPAddress.Parse(MainWindow.IntIPAddr.ToString().Substring(
                        0, MainWindow.IntIPAddr.ToString().LastIndexOf(".", StringComparison.Ordinal)) +".1") : DnsSettings.EDnsIp;
            else if (Equals(clientAddress, IPAddress.Loopback) || IpTools.InSameLaNet(clientAddress, MainWindow.LocIPAddr))
                clientAddress = MainWindow.IntIPAddr;

            DnsMessage response = query.CreateResponseInstance();

            if (query.Questions.Count <= 0)
                response.ReturnCode = ReturnCode.ServerFailure;

            else
            {
                foreach (DnsQuestion dnsQuestion in query.Questions)
                {
                    response.ReturnCode = ReturnCode.NoError;

                    if (DnsSettings.DebugLog)
                        BackgroundLog($@"| {DateTime.Now} {e.RemoteEndpoint.Address} : {dnsQuestion.Name} | {dnsQuestion.RecordType.ToString().ToUpper()}");

                    if (DnsSettings.DnsCacheEnable && MemoryCache.Default.Contains($"{dnsQuestion.Name}{dnsQuestion.RecordType}"))
                    {
                        response.AnswerRecords.AddRange(
                            (List<DnsRecordBase>) MemoryCache.Default.Get($"{dnsQuestion.Name}{dnsQuestion.RecordType}"));
                        response.AnswerRecords.Add(new TxtRecord(DomainName.Parse("cache.auroradns.mili.one"), 0, "AuroraDNSC Cached"));
                        if (DnsSettings.DebugLog)
                            BackgroundLog($@"|- CacheContains : {dnsQuestion.Name} | Count : {MemoryCache.Default.Count()}");
                    }
                    else if (DnsSettings.BlackListEnable && DnsSettings.BlackList.Contains(dnsQuestion.Name) && dnsQuestion.RecordType == RecordType.A)
                    {
                        response.AnswerRecords.Add(new ARecord(dnsQuestion.Name, 10, IPAddress.Any));
                        response.AnswerRecords.Add(new TxtRecord(DomainName.Parse("blacklist.auroradns.mili.one"), 0, "AuroraDNSC Blocked"));
                        if (DnsSettings.DebugLog)
                            BackgroundLog(@"|- BlackList");
                    }
                    else if (DnsSettings.WhiteListEnable && DnsSettings.WhiteList.ContainsKey(dnsQuestion.Name) && dnsQuestion.RecordType == RecordType.A)
                    {
                        List<DnsRecordBase> whiteRecords = new List<DnsRecordBase>();
                        if (!IpTools.IsIp(DnsSettings.WhiteList[dnsQuestion.Name]))
                            whiteRecords.AddRange(new DnsClient(DnsSettings.SecondDnsIp, 1000)
                                .Resolve(dnsQuestion.Name, dnsQuestion.RecordType).AnswerRecords);
                        else
                            whiteRecords.Add(new ARecord(dnsQuestion.Name, 10,
                                IPAddress.Parse(DnsSettings.WhiteList[dnsQuestion.Name])));

                        response.AnswerRecords.AddRange(whiteRecords);
                        response.AnswerRecords.Add(new TxtRecord(DomainName.Parse("whitelist.auroradns.mili.one"), 0, "AuroraDNSC Rewrote"));
                        if (DnsSettings.DebugLog)
                            BackgroundLog(@"|- WhiteList");
                    }
                    else
                    {
                        //Resolve
                        try
                        {
                            (List<DnsRecordBase> resolvedDnsList, ReturnCode statusCode) = DnsSettings.DnsMsgEnable
                                ? ResolveOverHttpsByDnsMsg(clientAddress.ToString(),
                                    dnsQuestion.Name.ToString(), DnsSettings.HttpsDnsUrl, DnsSettings.ProxyEnable,
                                    DnsSettings.WProxy, dnsQuestion.RecordType)
                                : ResolveOverHttpsByDnsJson(clientAddress.ToString(),
                                    dnsQuestion.Name.ToString(), DnsSettings.HttpsDnsUrl, DnsSettings.ProxyEnable,
                                    DnsSettings.WProxy, dnsQuestion.RecordType);

                            if (resolvedDnsList != null && resolvedDnsList.Count != 0 && statusCode == ReturnCode.NoError)
                            {
                                response.AnswerRecords.AddRange(resolvedDnsList);

                                if (DnsSettings.DnsCacheEnable)
                                    BackgroundWriteCache(
                                        new CacheItem($"{dnsQuestion.Name}{dnsQuestion.RecordType}", resolvedDnsList),
                                        resolvedDnsList[0].TimeToLive);
                            }
                            else if (statusCode == ReturnCode.ServerFailure)
                            {
                                response.AnswerRecords = new DnsClient(DnsSettings.SecondDnsIp, 1000)
                                    .Resolve(dnsQuestion.Name, dnsQuestion.RecordType).AnswerRecords;
                                BackgroundLog($"| -- SecondDns : {DnsSettings.SecondDnsIp}");
                            }
                            else
                                response.ReturnCode = statusCode;
                        }
                        catch (Exception ex)
                        {
                            response.ReturnCode = ReturnCode.ServerFailure;
                            BackgroundLog(@"| " + ex);
                        }
                    }

                }
            }
            e.Response = response;
        }

        private static (List<DnsRecordBase> list, ReturnCode statusCode) ResolveOverHttpsByDnsJson(string clientIpAddress,
            string domainName, string dohUrl,
            bool proxyEnable = false, IWebProxy wProxy = null, RecordType type = RecordType.A)
        {
            string dnsStr;
            List<DnsRecordBase> recordList = new List<DnsRecordBase>();

            try
            {
                dnsStr = MyCurl.GetString(dohUrl + @"?ct=application/dns-json&" +
                                          $"name={domainName}&type={type.ToString().ToUpper()}&edns_client_subnet={clientIpAddress}",
                    DnsSettings.Http2Enable, proxyEnable, wProxy);
            }
            catch (WebException e)
            {
                HttpWebResponse response = (HttpWebResponse) e.Response;
                try
                {
                    BackgroundLog($@"| - Catch WebException : {Convert.ToInt32(response.StatusCode)} {response.StatusCode} | {domainName} | {response.ResponseUri}");
                }
                catch (Exception exception)
                {
                    BackgroundLog($@"| - Catch WebException : {exception.Message} | {domainName} | {dohUrl}");
                    //MainWindow.NotifyIcon.ShowBalloonTip(360, "AuroraDNS - 错误",
                    //    $"异常 : {exception.Message} {Environment.NewLine} {domainName}", ToolTipIcon.Warning);
                }

                if (dohUrl != DnsSettings.HttpsDnsUrl) return (new List<DnsRecordBase>(), ReturnCode.ServerFailure);
                BackgroundLog($@"| -- SecondDoH : {DnsSettings.SecondHttpsDnsUrl}");
                return ResolveOverHttpsByDnsJson(clientIpAddress, domainName, DnsSettings.SecondHttpsDnsUrl,
                    proxyEnable, wProxy, type);
            }

            JsonValue dnsJsonValue = Json.Parse(dnsStr);

            int statusCode = dnsJsonValue.AsObjectGetInt("Status");
            if (statusCode != 0)
                return (new List<DnsRecordBase>(), (ReturnCode) statusCode);

            if (dnsStr.Contains("\"Answer\""))
            {
                var dnsAnswerJsonList = dnsJsonValue.AsObjectGetArray("Answer");

                foreach (var itemJsonValue in dnsAnswerJsonList)
                {
                    string answerAddr = itemJsonValue.AsObjectGetString("data");
                    string answerDomainName = itemJsonValue.AsObjectGetString("name");
                    int answerType = itemJsonValue.AsObjectGetInt("type");
                    int ttl = itemJsonValue.AsObjectGetInt("TTL");

                    switch (type)
                    {
                        case RecordType.A:
                        {
                            if (Convert.ToInt32(RecordType.A) == answerType)
                            {
                                ARecord aRecord = new ARecord(
                                    DomainName.Parse(answerDomainName), ttl, IPAddress.Parse(answerAddr));

                                recordList.Add(aRecord);
                            }
                            else if (Convert.ToInt32(RecordType.CName) == answerType)
                            {
                                CNameRecord cRecord = new CNameRecord(
                                    DomainName.Parse(answerDomainName), ttl, DomainName.Parse(answerAddr));

                                recordList.Add(cRecord);

                                //recordList.AddRange(ResolveOverHttps(clientIpAddress,answerAddr));
                                //return recordList;
                            }

                            break;
                        }

                        case RecordType.Aaaa:
                        {
                            if (Convert.ToInt32(RecordType.Aaaa) == answerType)
                            {
                                AaaaRecord aaaaRecord = new AaaaRecord(
                                    DomainName.Parse(answerDomainName), ttl, IPAddress.Parse(answerAddr));
                                recordList.Add(aaaaRecord);
                            }
                            else if (Convert.ToInt32(RecordType.CName) == answerType)
                            {
                                CNameRecord cRecord = new CNameRecord(
                                    DomainName.Parse(answerDomainName), ttl, DomainName.Parse(answerAddr));
                                recordList.Add(cRecord);
                            }

                            break;
                        }

                        case RecordType.CName when answerType == Convert.ToInt32(RecordType.CName):
                        {
                            CNameRecord cRecord = new CNameRecord(
                                DomainName.Parse(answerDomainName), ttl, DomainName.Parse(answerAddr));
                            recordList.Add(cRecord);
                            break;
                        }

                        case RecordType.Ns when answerType == Convert.ToInt32(RecordType.Ns):
                        {
                            NsRecord nsRecord = new NsRecord(
                                DomainName.Parse(answerDomainName), ttl, DomainName.Parse(answerAddr));
                            recordList.Add(nsRecord);
                            break;
                        }

                        case RecordType.Mx when answerType == Convert.ToInt32(RecordType.Mx):
                        {
                            MxRecord mxRecord = new MxRecord(
                                DomainName.Parse(answerDomainName), ttl,
                                ushort.Parse(answerAddr.Split(' ')[0]),
                                DomainName.Parse(answerAddr.Split(' ')[1]));
                            recordList.Add(mxRecord);
                            break;
                        }

                        case RecordType.Txt when answerType == Convert.ToInt32(RecordType.Txt):
                        {
                            TxtRecord txtRecord = new TxtRecord(DomainName.Parse(answerDomainName), ttl, answerAddr);
                            recordList.Add(txtRecord);
                            break;
                        }

                        case RecordType.Ptr when answerType == Convert.ToInt32(RecordType.Ptr):
                        {
                            PtrRecord ptrRecord = new PtrRecord(
                                DomainName.Parse(answerDomainName), ttl, DomainName.Parse(answerAddr));
                            recordList.Add(ptrRecord);
                            break;
                        }

                        default:
                        {
                            statusCode = Convert.ToInt32(ReturnCode.ServerFailure);
                            break;
                        }
                    }
                }
            }

            return (recordList, (ReturnCode) statusCode);
        }

        private static (List<DnsRecordBase> list, ReturnCode statusCode) ResolveOverHttpsByDnsMsg(string clientIpAddress, string domainName, string dohUrl,
            bool proxyEnable = false, IWebProxy wProxy = null, RecordType type = RecordType.A)
        {
            DnsMessage dnsMsg;
            var dnsBase64String = Convert.ToBase64String(MyDnsSend.GetQuestionData(domainName.TrimEnd('.'), type)).TrimEnd('=')
                .Replace('+', '-').Replace('/', '_');

            try
            {
                var dnsDataBytes = MyCurl.GetData(
                    $"{dohUrl}?ct=application/dns-message&dns={dnsBase64String}&edns_client_subnet={clientIpAddress}",
                    DnsSettings.Http2Enable, proxyEnable, wProxy);
                 dnsMsg = DnsMessage.Parse(dnsDataBytes);
            }
            catch (WebException e)
            {
                HttpWebResponse response = (HttpWebResponse)e.Response;
                try
                {
                    BackgroundLog($@"| - Catch WebException : {Convert.ToInt32(response.StatusCode)} {response.StatusCode} | {domainName} | {dohUrl} | {dnsBase64String}");

                    if (response.StatusCode == HttpStatusCode.BadRequest)
                    {
                        DnsSettings.DnsMsgEnable = false;
                        return ResolveOverHttpsByDnsJson(clientIpAddress, domainName, DnsSettings.SecondHttpsDnsUrl,
                            proxyEnable, wProxy, type);
                    }
                }
                catch (Exception exception)
                {
                    BackgroundLog($@"| - Catch WebException : {exception.Message} | {domainName} | {dohUrl} | {dnsBase64String}");
                }

                if (dohUrl != DnsSettings.HttpsDnsUrl) return (new List<DnsRecordBase>(), ReturnCode.ServerFailure);
                BackgroundLog($@"| -- SecondDoH : {DnsSettings.SecondHttpsDnsUrl}");
                return ResolveOverHttpsByDnsMsg(clientIpAddress, domainName, DnsSettings.SecondHttpsDnsUrl,
                    proxyEnable, wProxy, type);
            }
            return (dnsMsg.AnswerRecords, dnsMsg.ReturnCode);
        }
    }
}
