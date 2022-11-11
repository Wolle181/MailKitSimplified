﻿using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Abstractions;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using MailKit;
using MailKit.Security;
using MailKit.Net.Imap;
using MailKitSimplified.Receiver.Abstractions;
using MailKitSimplified.Receiver.Models;
using MailKitSimplified.Receiver.Extensions;

namespace MailKitSimplified.Receiver.Services
{
    public class ImapClientService : IImapClientService
    {
        private readonly ILogger _logger;
        private readonly IImapClient _imapClient;
        private readonly EmailReceiverOptions _receiverOptions;

        public ImapClientService(IOptions<EmailReceiverOptions> receiverOptions, IProtocolLogger protocolLogger = null, ILogger<ImapClientService> logger = null)
        {
            _logger = logger ?? NullLogger<ImapClientService>.Instance;
            _receiverOptions = receiverOptions.Value;
            if (string.IsNullOrWhiteSpace(_receiverOptions.ImapHost))
                throw new NullReferenceException(nameof(EmailReceiverOptions.ImapHost));
            if (_receiverOptions.ImapCredential == null)
                _logger.LogWarning($"{nameof(EmailReceiverOptions.ImapCredential)} is null.");
            var imapLogger = protocolLogger ?? new MailKitProtocolLogger(_receiverOptions.ProtocolLog);
            _imapClient = imapLogger != null ? new ImapClient(imapLogger) : new ImapClient();
        }

        public static ImapClientService Create(string imapHost, ushort imapPort = 0, string username = null, string password = null, string protocolLog = null)
        {
            var imapCredential = username == null && password == null ? null : new NetworkCredential(username ?? "", password ?? "");
            var receiver = Create(imapHost, imapCredential, imapPort, protocolLog);
            return receiver;
        }

        public static ImapClientService Create(string imapHost, NetworkCredential imapCredential, ushort imapPort = 0, string protocolLog = null)
        {
            var receiverOptions = new EmailReceiverOptions(imapHost, imapCredential, imapPort, protocolLog);
            var receiver = Create(receiverOptions);
            return receiver;
        }

        public static ImapClientService Create(EmailReceiverOptions emailReceiverOptions)
        {
            var options = Options.Create(emailReceiverOptions);
            var receiver = new ImapClientService(options);
            return receiver;
        }

        /// <exception cref="AuthenticationException">Failed to authenticate</exception>
        public virtual async ValueTask AuthenticateAsync(CancellationToken cancellationToken = default)
        {
            if (!_imapClient.IsConnected)
            {
                await _imapClient.ConnectAsync(_receiverOptions.ImapHost, _receiverOptions.ImapPort, SecureSocketOptions.Auto, cancellationToken).ConfigureAwait(false);
                if (_imapClient.Capabilities.HasFlag(ImapCapabilities.Compress))
                    await _imapClient.CompressAsync(cancellationToken).ConfigureAwait(false);
            }
            if (!_imapClient.IsAuthenticated)
            {
                var ntlm = _imapClient.AuthenticationMechanisms.Contains("NTLM") ?
                    new SaslMechanismNtlm(_receiverOptions.ImapCredential) : null;
                if (ntlm?.Workstation != null)
                    await _imapClient.AuthenticateAsync(ntlm, cancellationToken).ConfigureAwait(false);
                else
                    await _imapClient.AuthenticateAsync(_receiverOptions.ImapCredential, cancellationToken).ConfigureAwait(false);
            }
        }

        public async ValueTask<IMailFolder> ConnectAsync(CancellationToken ct = default)
        {
            await AuthenticateAsync(ct).ConfigureAwait(false);
            var mailFolder = await GetFolderAsync(_receiverOptions.MailFolderName).ConfigureAwait(false);
            return mailFolder;
        }

        /// <exception cref="AuthenticationException">Failed to authenticate</exception>
        /// <exception cref="FolderNotFoundException">No mail folder has the specified name</exception>
        public async ValueTask<IMailFolder> GetFolderAsync(string mailFolderName, CancellationToken ct = default)
        {
            await AuthenticateAsync(ct).ConfigureAwait(false);
            var mailFolder = string.IsNullOrWhiteSpace(mailFolderName) || mailFolderName.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ?
                _imapClient.Inbox : await _imapClient.GetFolderAsync(mailFolderName, ct).ConfigureAwait(false);
            return mailFolder;
        }

        protected async Task<IList<string>> GetFolderListAsync(CancellationToken ct = default)
        {
            IList<string> mailFolderNames = new List<string>();
            await AuthenticateAsync(ct).ConfigureAwait(false);
            if (_imapClient.PersonalNamespaces.Count > 0)
            {
                var rootFolder = await _imapClient.GetFoldersAsync(_imapClient.PersonalNamespaces[0]);
                var subfolders = rootFolder.SelectMany(rf => rf.GetSubfolders().Select(sf => sf.Name));
                var inboxSubfolders = _imapClient.Inbox.GetSubfolders().Select(f => f.FullName);
                mailFolderNames.AddRange(inboxSubfolders);
                mailFolderNames.AddRange(subfolders);
                _logger.LogDebug("{0} Inbox folders: {1}", subfolders.Count(), inboxSubfolders.ToEnumeratedString());
                _logger.LogDebug("{0} personal folders: {1}", subfolders.Count(), subfolders.ToEnumeratedString());
            }
            if (_imapClient.SharedNamespaces.Count > 0)
            {
                var rootFolder = await _imapClient.GetFoldersAsync(_imapClient.SharedNamespaces[0]);
                var subfolders = rootFolder.SelectMany(rf => rf.GetSubfolders().Select(sf => sf.Name));
                mailFolderNames.AddRange(subfolders);
                _logger.LogDebug("{0} shared folders: {1}", subfolders.Count(), subfolders.ToEnumeratedString());
            }
            if (_imapClient.OtherNamespaces.Count > 0)
            {
                var rootFolder = await _imapClient.GetFoldersAsync(_imapClient.OtherNamespaces[0]);
                var subfolders = rootFolder.SelectMany(rf => rf.GetSubfolders().Select(sf => sf.Name));
                mailFolderNames.AddRange(subfolders);
                _logger.LogDebug("{0} other folders: {1}", subfolders.Count(), subfolders.ToEnumeratedString());
            }
            return mailFolderNames;
        }

        public virtual void Disconnect()
        {
            if (_imapClient?.IsConnected ?? false)
            {
                lock (_imapClient.SyncRoot)
                    _imapClient?.Disconnect(true);
            }
        }

        public virtual void Dispose()
        {
            _logger.LogTrace("Disposing of the IMAP email receiver client...");
            Disconnect();
            _imapClient?.Dispose();
        }
    }
}
