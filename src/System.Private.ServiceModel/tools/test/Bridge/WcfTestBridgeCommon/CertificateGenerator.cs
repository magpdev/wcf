﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO;
using System.Net;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Prng;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Org.BouncyCastle.X509.Extension;
using X509Certificate2 = System.Security.Cryptography.X509Certificates.X509Certificate2;
using X509KeyStorageFlags = System.Security.Cryptography.X509Certificates.X509KeyStorageFlags;

namespace WcfTestBridgeCommon
{
    // Not threadsafe. Callers should lock.
    public class CertificateGenerator
    {
        private bool _isInitialized;

        // Settable properties prior to initialization
        private string _crlUri;
        private string _crlUriBridgeHost;
        private string _password;
        private TimeSpan _validityPeriod = TimeSpan.FromDays(1);

        // This can't be too short as there might be a time skew between machines,
        // but also can't be too long, as the CRL is cached by the machine
        private TimeSpan _crlValidityPeriod = TimeSpan.FromMinutes(2);

        // Give the cert a grace period in case there's a time skew between machines
        private readonly TimeSpan _gracePeriod = TimeSpan.FromHours(1);

        private const string _authorityCanonicalName = "DO_NOT_TRUST_WcfBridgeRootCA";
        private const string _crlUriRelativePath = "/resource/WcfService.CertificateResources.CertificateRevocationListResource";
        private const string _signatureAlthorithm = "SHA1WithRSAEncryption";
        private const int _keyLengthInBits = 2048;
        
        private static readonly X509V3CertificateGenerator _certGenerator = new X509V3CertificateGenerator();
        private static readonly X509V2CrlGenerator _crlGenerator = new X509V2CrlGenerator();

        // key: thumbprint, value: revocation time
        private static Dictionary<string, DateTime> _revokedCertificates = new Dictionary<string,DateTime>(); 
        
        private RsaKeyPairGenerator _keyPairGenerator;
        private SecureRandom _random;

        private DateTime _initializationDateTime;
        private DateTime _validityNotBefore;
        private DateTime _validityNotAfter;

        // We need to hang onto the _authorityKeyPair and _authorityCertificate - all certificates generated 
        // by this instance will be signed by this Authority certificate and private key
        private AsymmetricCipherKeyPair _authorityKeyPair;
        private X509CertificateContainer _authorityCertificate;

        public void Initialize()
        {
            if (!_isInitialized)
            {
                if (string.IsNullOrWhiteSpace(_authorityCanonicalName))
                {
                    throw new ArgumentException("AuthorityCanonicalName must not be an empty string or only whitespace", "AuthorityCanonicalName");
                }

                if (string.IsNullOrWhiteSpace(_password))
                {
                    throw new ArgumentException("Password must not be an empty string or only whitespace", "Password");
                }

                Uri dummy;
                if (string.IsNullOrWhiteSpace(_crlUriRelativePath) && !Uri.TryCreate(_crlUriRelativePath, UriKind.Relative, out dummy))
                {
                    throw new ArgumentException("CrlUri must be a valid relative URI", "CrlUriRelativePath");
                }

                _crlUri = string.Format("{0}{1}", _crlUriBridgeHost, _crlUriRelativePath); 

                _initializationDateTime = DateTime.UtcNow;
                _validityNotBefore = _initializationDateTime.Subtract(_gracePeriod);
                _validityNotAfter = _initializationDateTime.Add(_validityPeriod);

                _random = new SecureRandom(new CryptoApiRandomGenerator());
                _keyPairGenerator = new RsaKeyPairGenerator();
                _keyPairGenerator.Init(new KeyGenerationParameters(_random, _keyLengthInBits));
                _authorityKeyPair = _keyPairGenerator.GenerateKeyPair();

                _isInitialized = true;

                Trace.WriteLine("[CertificateGenerator] initialized with the following configuration:");
                Trace.WriteLine(string.Format("    {0} = {1}", "AuthorityCanonicalName", _authorityCanonicalName));
                Trace.WriteLine(string.Format("    {0} = {1}", "CrlUri", _crlUri));
                Trace.WriteLine(string.Format("    {0} = {1}", "Password", _password));
                Trace.WriteLine(string.Format("    {0} = {1}", "ValidityPeriod", _validityPeriod));
                Trace.WriteLine(string.Format("    {0} = {1}", "Valid to", _validityNotAfter));

                _authorityCertificate = CreateCertificate(true, null, string.Empty);
            }
        }

        public void Reset()
        {
            _certGenerator.Reset();
            _crlGenerator.Reset();
            _authorityCertificate = null;
            _isInitialized = false;
        }

        public X509CertificateContainer AuthorityCertificate
        {
            get
            {
                EnsureInitialized();
                return _authorityCertificate;
            }
        }

        public byte[] CrlEncoded
        {
            get
            {
                EnsureInitialized(); 
                return CreateCrl(_authorityCertificate.InternalCertificate).GetEncoded();
            }
        }

        public bool Initialized
        {
            get { return _isInitialized; }
        }

        public string AuthorityCanonicalName
        {
            get { return _authorityCanonicalName; }
        }

        public string AuthorityDistinguishedName
        {
            get
            {
                EnsureInitialized(); 
                return CreateX509Name(_authorityCanonicalName).ToString(); 
            }
        }

        public string CertificatePassword
        {
            get { return _password; }
            set
            {
                EnsureNotInitialized("CertificatePassword");
                _password = value; 
            }
        }

        public string CrlUri
        {
            get
            {
                EnsureInitialized(); 
                return _crlUri;
            }
        }

        public string CrlUriBridgeHost
        {
            get
            {
                return _crlUriBridgeHost; 
            }
            set
            {
                EnsureNotInitialized("CrlUriBridgeHost");
                _crlUriBridgeHost = value; 
            }
        }

        public string CrlUriRelativePath
        {
            get { return _crlUriRelativePath; }
        }

        public List<string> RevokedCertificates
        {
            get
            {
                List<string> retVal = new List<string>(_revokedCertificates.Keys);
                return retVal; 
            }
        }

        public TimeSpan ValidityPeriod
        {
            get { return _validityPeriod; }
            set
            {
                EnsureNotInitialized("ValidityPeriod");
                _validityPeriod = value; 
            }
        }

        public X509CertificateContainer CreateCertificate(params string[] subjects)
        {
            EnsureInitialized();
            return CreateCertificate(false, _authorityCertificate.InternalCertificate, subjects);
        }

        // Only the ctor should be calling with isAuthority = true
        private X509CertificateContainer CreateCertificate(bool isAuthority, X509Certificate signingCertificate, params string[] subjects)
        {
            if (!isAuthority ^ (signingCertificate != null))
            {
                throw new ArgumentException("Either isAuthority == true or signingCertificate is not null");
            }

            if (!isAuthority && (subjects == null || subjects.Length == 0))
            {
                throw new ArgumentException("If not creating an authority, must specify at least one Subject", "subjects");
            }

            if (!isAuthority && string.IsNullOrWhiteSpace(subjects[0]))
            {
                throw new ArgumentException("Certificate Subject must not be an empty string or only whitespace", "subjects");
            }

            EnsureInitialized();

            _certGenerator.Reset();
            _certGenerator.SetSignatureAlgorithm(_signatureAlthorithm);

            X509Name authorityX509Name = CreateX509Name(_authorityCanonicalName);
            
            var keyPair = isAuthority ? _authorityKeyPair : _keyPairGenerator.GenerateKeyPair();
            if (isAuthority)
            {
                _certGenerator.SetIssuerDN(authorityX509Name);
                _certGenerator.SetSubjectDN(authorityX509Name);

                var authorityKeyIdentifier = new AuthorityKeyIdentifier(
                    SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(_authorityKeyPair.Public),
                    new GeneralNames(new GeneralName(authorityX509Name)),
                    new BigInteger(7, _random).Abs());

                _certGenerator.AddExtension(X509Extensions.AuthorityKeyIdentifier, true, authorityKeyIdentifier);
                _certGenerator.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(X509KeyUsage.DigitalSignature | X509KeyUsage.KeyAgreement | X509KeyUsage.KeyCertSign | X509KeyUsage.KeyEncipherment | X509KeyUsage.CrlSign));

            }
            else
            {
                X509Name subjectName = CreateX509Name(subjects[0]);
                _certGenerator.SetIssuerDN(PrincipalUtilities.GetSubjectX509Principal(signingCertificate));
                _certGenerator.SetSubjectDN(subjectName);

                _certGenerator.AddExtension(X509Extensions.AuthorityKeyIdentifier, true, new AuthorityKeyIdentifierStructure(_authorityKeyPair.Public));
                _certGenerator.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(X509KeyUsage.DigitalSignature | X509KeyUsage.KeyAgreement | X509KeyUsage.KeyEncipherment));

            }

            _certGenerator.AddExtension(X509Extensions.SubjectKeyIdentifier, false, new SubjectKeyIdentifierStructure(keyPair.Public));

            _certGenerator.SetSerialNumber(new BigInteger(64 /*sizeInBits*/, _random).Abs());
            _certGenerator.SetNotBefore(_validityNotBefore);
            _certGenerator.SetNotAfter(_validityNotAfter);
            _certGenerator.SetPublicKey(keyPair.Public);

            _certGenerator.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(isAuthority));
            _certGenerator.AddExtension(X509Extensions.ExtendedKeyUsage, true, new ExtendedKeyUsage(KeyPurposeID.IdKPServerAuth, KeyPurposeID.IdKPClientAuth));

            if (!isAuthority)
            {
                var subjectAlternativeNames = new Asn1Encodable[subjects.Length];

                for (int i = 0; i < subjects.Length; i++)
                {
                    subjectAlternativeNames[i] = new GeneralName(GeneralName.DnsName, subjects[i]);
                }

                _certGenerator.AddExtension(X509Extensions.SubjectAlternativeName, false, new DerSequence(subjectAlternativeNames));
            }

            var crlDistributionPoints = new DistributionPoint[1] {
                new DistributionPoint(new DistributionPointName(
                    new GeneralNames(new GeneralName(GeneralName.UniformResourceIdentifier, _crlUri))), null, new GeneralNames(new GeneralName(authorityX509Name)))
                };
            var revocationListExtension = new CrlDistPoint(crlDistributionPoints);
            _certGenerator.AddExtension(X509Extensions.CrlDistributionPoints, false, revocationListExtension);

            X509Certificate cert = _certGenerator.Generate(_authorityKeyPair.Private, _random);
            EnsureCertificateValidity(cert);

            // For now, given that we don't know what format to return it in, preserve the formats so we have 
            // the flexibility to do what we need to

            X509CertificateContainer container = new X509CertificateContainer(); 

            X509CertificateEntry[] chain = new X509CertificateEntry[1];
            chain[0] = new X509CertificateEntry(cert);

            Pkcs12Store store = new Pkcs12StoreBuilder().Build();
            store.SetKeyEntry("", new AsymmetricKeyEntry(keyPair.Private), chain);

            using (MemoryStream stream = new MemoryStream())
            {
                store.Save(stream, _password.ToCharArray(), _random);
                container.Pfx = stream.ToArray(); 
            }

            X509Certificate2 outputCert;
            if (isAuthority)
            {
                // don't hand out the private key for the cert when it's the authority
                outputCert = new X509Certificate2(cert.GetEncoded()); 
            } 
            else
            {
                outputCert = new X509Certificate2(container.Pfx, _password, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
            }

            container.Subject = subjects[0];
            container.InternalCertificate = cert;
            container.Certificate = outputCert;
            container.Thumbprint = outputCert.Thumbprint;

            Trace.WriteLine("[CertificateGenerator] generated a certificate:");
            Trace.WriteLine(string.Format("    {0} = {1}", "isAuthority", isAuthority));
            if (!isAuthority)
            {
                Trace.WriteLine(string.Format("    {0} = {1}", "Signed by", signingCertificate.SubjectDN));
                Trace.WriteLine(string.Format("    {0} = {1}", "Subject (CN) ", subjects[0]));
                Trace.WriteLine(string.Format("    {0} = {1}", "Alt names ", string.Join(", ", subjects)));
            }
            Trace.WriteLine(string.Format("    {0} = {1}", "HasPrivateKey:", outputCert.HasPrivateKey));
            Trace.WriteLine(string.Format("    {0} = {1}", "Thumbprint", outputCert.Thumbprint));

            return container;
        }

        private X509Crl CreateCrl(X509Certificate signingCertificate)
        {
            EnsureInitialized();
            
            _crlGenerator.Reset();

            _crlGenerator.SetThisUpdate(_initializationDateTime);
            _crlGenerator.SetNextUpdate(DateTime.UtcNow.Add(_crlValidityPeriod));
            _crlGenerator.SetIssuerDN(PrincipalUtilities.GetSubjectX509Principal(signingCertificate));
            _crlGenerator.SetSignatureAlgorithm(_signatureAlthorithm);

            _crlGenerator.AddExtension(X509Extensions.AuthorityKeyIdentifier, false, new AuthorityKeyIdentifierStructure(signingCertificate));

            BigInteger crlNumber = new BigInteger(7 /*bits for the number*/, _random).Abs();
            _crlGenerator.AddExtension(X509Extensions.CrlNumber, false, new CrlNumber(crlNumber));

            foreach (var kvp in _revokedCertificates)
            {
                _crlGenerator.AddCrlEntry(new BigInteger(kvp.Key, 16), kvp.Value, CrlReason.CessationOfOperation); 
            }

            X509Crl crl = _crlGenerator.Generate(_authorityKeyPair.Private, _random);
            crl.Verify(_authorityKeyPair.Public);

            Trace.WriteLine(string.Format("[CertificateGenerator] has created a Certificate Revocation List :"));
            Trace.WriteLine(string.Format("    {0} = {1}", "Issuer", crl.IssuerDN));
            Trace.WriteLine(string.Format("    {0} = {1}", "CRL Number", crlNumber));

            return crl;
        }

        private void EnsureCertificateValidity(X509Certificate certificate)
        {
            certificate.CheckValidity(DateTime.UtcNow);
            certificate.Verify(_authorityKeyPair.Public);
        }

        private void EnsureInitialized()
        {
            if (!_isInitialized)
            {
                Initialize();
            }
        }

        private void EnsureNotInitialized(string paramName)
        {
            if (_isInitialized)
            {
                throw new ArgumentException(paramName + " cannot be set as the generator has already been initialized.", paramName);
            }
        }

        private static X509Name CreateX509Name(string canonicalName)
        {
            X509Name authorityX509Name;

            IList authorityKeyIdOrder = new ArrayList();
            IDictionary authorityKeyIdName = new Hashtable();

            authorityKeyIdOrder.Add(X509Name.OU);
            authorityKeyIdOrder.Add(X509Name.O);
            authorityKeyIdOrder.Add(X509Name.CN);

            authorityKeyIdName.Add(X509Name.CN, canonicalName);
            authorityKeyIdName.Add(X509Name.O, "DO_NOT_TRUST");
            authorityKeyIdName.Add(X509Name.OU, "Created by https://github.com/dotnet/wcf");

            authorityX509Name = new X509Name(authorityKeyIdOrder, authorityKeyIdName);

            return authorityX509Name;
        }

        public bool RevokeCertificateByThumbprint(string thumbprint)
        {
            bool success = false;
            BigInteger thumbprintBigInt = null;
            try
            {
                thumbprintBigInt = new BigInteger(thumbprint, 16 /* radix */);
                success = true;
            }
            catch
            {
            }

            if (success && !_revokedCertificates.ContainsKey(thumbprint))
            {
                _revokedCertificates.Add(thumbprint, DateTime.UtcNow);
            }

            return success;
        }

        public class X509CertificateContainer
        {
            public string Subject { get; internal set; }
            internal X509Certificate InternalCertificate { get; set; }
            public X509Certificate2 Certificate { get; internal set; }
            internal byte[] Pfx { get; set; }
            public string Thumbprint { get; internal set; }
        }
    }
}
