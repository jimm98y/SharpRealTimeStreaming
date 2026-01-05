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

            string password = Guid.NewGuid().ToString();
            using (var pkcs12Stream = new MemoryStream())
            {
                store.Save(pkcs12Stream, password.ToCharArray(), random);
                return System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12Collection(pkcs12Stream.ToArray(), password).Single();
            }
        }
    }
}
