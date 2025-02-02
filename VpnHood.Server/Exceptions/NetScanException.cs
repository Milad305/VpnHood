﻿using System.Net;
using Microsoft.Extensions.Logging;
using VpnHood.Common.Messaging;
using VpnHood.Tunneling;

namespace VpnHood.Server.Exceptions;

internal class NetScanException : ServerSessionException
{
    public NetScanException(IPEndPoint remoteEndPoint, Session session, string requestId)
        : base(remoteEndPoint, session, SessionErrorCode.GeneralError, requestId, "NetScan protector does not allow this request.")
    {
    }

    public override void Log()
    {
        //let EventReporter manage it
    }
    protected override LogLevel LogLevel => LogLevel.Warning;
    protected override EventId EventId => GeneralEventId.NetProtect;
}