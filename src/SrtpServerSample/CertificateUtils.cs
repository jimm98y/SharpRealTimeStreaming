// SharpRTSPServer
// Copyright (C) 2026 Lukas Volf
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Prng;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;

namespace SrtpServerSample
{
    public static class CertificateUtils
    {
        public static System.Security.Cryptography.X509Certificates.X509Certificate2 GenerateECDSAServerCertificate(
            string name,
            DateTime notBefore,
            DateTime notAfter,
            string curve = "secp256r1",
            string signatureAlgorithm = "SHA256WITHECDSA",
            int serialNumberLength = 20)
        {
            var randomGenerator = new CryptoApiRandomGenerator();
            var random = new SecureRandom(randomGenerator);

            var spec = ECNamedCurveTable.GetByName(curve);
            var curveOid = ECNamedCurveTable.GetOid(curve);
            var domainParams = new ECNamedDomainParameters(curveOid, spec.Curve, spec.G, spec.N, spec.H, spec.GetSeed());

            var keyPairGenerator = new ECKeyPairGenerator("EC");
            ECKeyGenerationParameters keyGenerationParameters = new ECKeyGenerationParameters(domainParams, random);
            keyPairGenerator.Init(keyGenerationParameters);

            AsymmetricCipherKeyPair subjectKeyPair = keyPairGenerator.GenerateKeyPair();
            AsymmetricCipherKeyPair issuerKeyPair = subjectKeyPair;
            ISignatureFactory signatureFactory = new Asn1SignatureFactory(signatureAlgorithm, issuerKeyPair.Private, random);

            var certificateGenerator = new X509V3CertificateGenerator();
            certificateGenerator.SetNotBefore(notBefore);
            certificateGenerator.SetNotAfter(notAfter);

            var nameOids = new List<DerObjectIdentifier>
            {
                X509Name.CN
            };

            var nameValues = new Dictionary<DerObjectIdentifier, string>()
            {
                { X509Name.CN, name }
            };

            var subjectDN = new X509Name(nameOids, nameValues);
            var issuerDN = subjectDN;

            certificateGenerator.SetIssuerDN(issuerDN);
            certificateGenerator.SetSubjectDN(subjectDN);
            certificateGenerator.SetPublicKey(issuerKeyPair.Public);

            // A certificate without these is rejected by anything that checks properly: TLS clients
            // match the host against the Subject Alternative Name, not the Common Name.
            certificateGenerator.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(false));
            certificateGenerator.AddExtension(X509Extensions.KeyUsage, true,
                new KeyUsage(KeyUsage.DigitalSignature | KeyUsage.KeyAgreement));
            certificateGenerator.AddExtension(X509Extensions.ExtendedKeyUsage, false,
                new ExtendedKeyUsage(KeyPurposeID.id_kp_serverAuth));
            certificateGenerator.AddExtension(X509Extensions.SubjectAlternativeName, false, BuildSubjectAlternativeName(name));

            byte[] serial = new byte[serialNumberLength];
            random.NextBytes(serial);
            serial[0] = 1;
            certificateGenerator.SetSerialNumber(new Org.BouncyCastle.Math.BigInteger(serial));

            X509Certificate x509Certificate = certificateGenerator.Generate(signatureFactory);
            AsymmetricKeyParameter privateKey = issuerKeyPair.Private;

            var certificateEntry = new Org.BouncyCastle.Pkcs.X509CertificateEntry(x509Certificate);
            string friendlyName = x509Certificate.SubjectDN.ToString();
            var store = new Pkcs12StoreBuilder().Build();
            store.SetCertificateEntry(friendlyName, certificateEntry);
            store.SetKeyEntry(friendlyName, new Org.BouncyCastle.Pkcs.AsymmetricKeyEntry(subjectKeyPair.Private), new[] { certificateEntry });

            // The store never leaves this method, but the password still guards the private key while
            // it is in memory, so it comes from the CSPRNG rather than from a GUID.
            byte[] passwordBytes = new byte[32];
            random.NextBytes(passwordBytes);
            char[] password = Convert.ToBase64String(passwordBytes).ToCharArray();

            using (var pkcs12Stream = new MemoryStream())
            {
                store.Save(pkcs12Stream, password, random);

                // The key deliberately uses the default key set rather than EphemeralKeySet.
                // SslStream on Windows goes through SChannel, which cannot build server credentials
                // from an ephemeral key and fails the handshake with "No credentials are available
                // in the security package". The cost is a key container Windows leaves behind, which
                // is the accepted trade for a certificate that actually works as a server identity.
                return System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12Collection(
                    pkcs12Stream.ToArray(),
                    new string(password)).Single();
            }
        }

        /// <summary>
        /// Puts the host name into the Subject Alternative Name, as an IP address when it is one and
        /// as a DNS name otherwise.
        /// </summary>
        private static GeneralNames BuildSubjectAlternativeName(string name)
        {
            var generalName = IPAddress.TryParse(name, out _)
                ? new GeneralName(GeneralName.IPAddress, name)
                : new GeneralName(GeneralName.DnsName, name);

            return new GeneralNames(generalName);
        }
    }
}
