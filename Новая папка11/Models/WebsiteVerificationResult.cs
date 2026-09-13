using System;
using System.Collections.Generic;

namespace TextFileProcessor.Models
{
    public sealed class WebsiteVerificationResult
    {
        public string Domain { get; set; } = string.Empty;

        public bool DnsResolved { get; set; }

        public List<string> IpAddresses { get; set; } = new();

        public bool HttpAvailable { get; set; }

        public int? HttpStatusCode { get; set; }

        public string HttpFinalUrl { get; set; } = string.Empty;

        public bool HttpsAvailable { get; set; }

        public int? HttpsStatusCode { get; set; }

        public string HttpsFinalUrl { get; set; } = string.Empty;

        public bool CertificatePresent { get; set; }

        public bool CertificateValid { get; set; }

        public string CertificateSubject { get; set; } = string.Empty;

        public string CertificateIssuer { get; set; } = string.Empty;

        public DateTimeOffset? CertificateExpiresAt { get; set; }

        public string CertificateError { get; set; } = string.Empty;

        public string ControlText { get; set; } = string.Empty;

        public bool ControlTextRequired { get; set; }

        public bool ControlTextFound { get; set; }

        public TimeSpan Duration { get; set; }

        public List<string> Errors { get; set; } = new();

        public bool Success =>
            DnsResolved &&
            HttpsAvailable &&
            CertificatePresent &&
            CertificateValid &&
            (!ControlTextRequired || ControlTextFound);
    }
}