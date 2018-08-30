﻿using System;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using libhac.Streams;

namespace libhac
{
    public static class Crypto
    {
        internal const int Aes128Size = 0x10;
        internal const int Sha256DigestSize = 0x20;

        public static void DecryptEcb(byte[] key, byte[] src, int srcIndex, byte[] dest, int destIndex, int length)
        {
            using (var aes = Aes.Create())
            {
                if (aes == null) throw new CryptographicException("Unable to create AES object");
                aes.Key = key;
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                var dec = aes.CreateDecryptor();
                using (var ms = new MemoryStream(dest, destIndex, length))
                using (var cs = new CryptoStream(ms, dec, CryptoStreamMode.Write))
                {
                    cs.Write(src, srcIndex, length);
                    cs.FlushFinalBlock();
                }
            }
        }

        public static void DecryptEcb(byte[] key, byte[] src, byte[] dest, int length) =>
            DecryptEcb(key, src, 0, dest, 0, length);

        public static void DecryptCbc(byte[] key, byte[] iv, byte[] src, int srcIndex, byte[] dest, int destIndex, int length)
        {
            using (var aes = Aes.Create())
            {
                if (aes == null) throw new CryptographicException("Unable to create AES object");
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                var dec = aes.CreateDecryptor();
                using (var ms = new MemoryStream(dest, destIndex, length))
                using (var cs = new CryptoStream(ms, dec, CryptoStreamMode.Write))
                {
                    cs.Write(src, srcIndex, length);
                    cs.FlushFinalBlock();
                }
            }
        }

        public static void DecryptCbc(byte[] key, byte[] iv, byte[] src, byte[] dest, int length) =>
            DecryptCbc(key, iv, src, 0, dest, 0, length);

        public static void GenerateKek(byte[] dst, byte[] src, byte[] masterKey, byte[] kekSeed, byte[] keySeed)
        {
            var kek = new byte[Aes128Size];
            var srcKek = new byte[Aes128Size];
            DecryptEcb(masterKey, kekSeed, kek, Aes128Size);
            DecryptEcb(kek, src, srcKek, Aes128Size);

            if (keySeed != null)
            {
                DecryptEcb(srcKek, keySeed, dst, Aes128Size);
            }
        }

        private static BigInteger GetBigInteger(byte[] bytes)
        {
            var signPadded = new byte[bytes.Length + 1];
            Buffer.BlockCopy(bytes, 0, signPadded, 1, bytes.Length);
            Array.Reverse(signPadded);
            return new BigInteger(signPadded);
        }

        public static RSAParameters DecryptRsaKey(byte[] encryptedKey, byte[] kek)
        {
            var counter = new byte[0x10];
            Array.Copy(encryptedKey, counter, 0x10);
            var body = new byte[0x230];
            Array.Copy(encryptedKey, 0x10, body, 0, 0x230);
            var dec = new byte[0x230];

            using (var streamDec = new RandomAccessSectorStream(new Aes128CtrStream(new MemoryStream(body), kek, counter)))
            {
                streamDec.Read(dec, 0, dec.Length);
            }

            var d = new byte[0x100];
            var n = new byte[0x100];
            var e = new byte[4];
            Array.Copy(dec, 0, d, 0, 0x100);
            Array.Copy(dec, 0x100, n, 0, 0x100);
            Array.Copy(dec, 0x200, e, 0, 4);

            var dInt = GetBigInteger(d);
            var nInt = GetBigInteger(n);
            var eInt = GetBigInteger(e);

            RSAParameters rsaParams = RecoverRsaParameters(nInt, eInt, dInt);
            TestRsaKey(rsaParams);
            return rsaParams;
        }

        private static void TestRsaKey(RSAParameters keyParams)
        {
            var rsa = new RSACryptoServiceProvider();
            rsa.ImportParameters(keyParams);

            var test = new byte[] {12, 34, 56, 78};
            byte[] testEnc = rsa.Encrypt(test, false);
            byte[] testDec = rsa.Decrypt(testEnc, false);

            if (!Util.ArraysEqual(test, testDec))
            {
                throw new InvalidDataException("Could not verify RSA key pair");
            }
        }

        public static byte[] DecryptTitleKey(byte[] titleKeyblock, RSAParameters rsaParams)
        {
#if USE_RSA_CNG
            RSA rsa = new RSACng();
#else
            RSA rsa = RSA.Create();
#endif
            rsa.ImportParameters(rsaParams);
            return rsa.Decrypt(titleKeyblock, RSAEncryptionPadding.OaepSHA256);
        }

        private static RSAParameters RecoverRsaParameters(BigInteger n, BigInteger e, BigInteger d)
        {
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                BigInteger k = d * e - 1;

                if (!k.IsEven)
                {
                    throw new InvalidOperationException("d*e - 1 is odd");
                }

                BigInteger two = 2;
                BigInteger t = BigInteger.One;

                BigInteger r = k / two;

                while (r.IsEven)
                {
                    t++;
                    r /= two;
                }

                byte[] rndBuf = n.ToByteArray();

                if (rndBuf[rndBuf.Length - 1] == 0)
                {
                    rndBuf = new byte[rndBuf.Length - 1];
                }

                BigInteger nMinusOne = n - BigInteger.One;

                bool cracked = false;
                BigInteger y = BigInteger.Zero;

                for (int i = 0; i < 100 && !cracked; i++)
                {
                    BigInteger g;

                    do
                    {
                        rng.GetBytes(rndBuf);
                        g = GetBigInteger(rndBuf);
                    }
                    while (g >= n);

                    y = BigInteger.ModPow(g, r, n);

                    if (y.IsOne || y == nMinusOne)
                    {
                        i--;
                        continue;
                    }

                    for (BigInteger j = BigInteger.One; j < t; j++)
                    {
                        BigInteger x = BigInteger.ModPow(y, two, n);

                        if (x.IsOne)
                        {
                            cracked = true;
                            break;
                        }

                        if (x == nMinusOne)
                        {
                            break;
                        }

                        y = x;
                    }
                }

                if (!cracked)
                {
                    throw new InvalidOperationException("Prime factors not found");
                }

                BigInteger p = BigInteger.GreatestCommonDivisor(y - BigInteger.One, n);
                BigInteger q = n / p;
                BigInteger dp = d % (p - BigInteger.One);
                BigInteger dq = d % (q - BigInteger.One);
                BigInteger inverseQ = ModInverse(q, p);

                int modLen = rndBuf.Length;
                int halfModLen = (modLen + 1) / 2;

                return new RSAParameters
                {
                    Modulus = GetBytes(n, modLen),
                    Exponent = GetBytes(e, -1),
                    D = GetBytes(d, modLen),
                    P = GetBytes(p, halfModLen),
                    Q = GetBytes(q, halfModLen),
                    DP = GetBytes(dp, halfModLen),
                    DQ = GetBytes(dq, halfModLen),
                    InverseQ = GetBytes(inverseQ, halfModLen),
                };
            }
        }

        private static byte[] GetBytes(BigInteger value, int size)
        {
            byte[] bytes = value.ToByteArray();

            if (size == -1)
            {
                size = bytes.Length;
            }

            if (bytes.Length > size + 1)
            {
                throw new InvalidOperationException($"Cannot squeeze value {value} to {size} bytes from {bytes.Length}.");
            }

            if (bytes.Length == size + 1 && bytes[bytes.Length - 1] != 0)
            {
                throw new InvalidOperationException($"Cannot squeeze value {value} to {size} bytes from {bytes.Length}.");
            }

            Array.Resize(ref bytes, size);
            Array.Reverse(bytes);
            return bytes;
        }

        private static BigInteger ModInverse(BigInteger e, BigInteger n)
        {
            BigInteger r = n;
            BigInteger newR = e;
            BigInteger t = 0;
            BigInteger newT = 1;

            while (newR != 0)
            {
                BigInteger quotient = r / newR;
                BigInteger temp;

                temp = t;
                t = newT;
                newT = temp - quotient * newT;

                temp = r;
                r = newR;
                newR = temp - quotient * newR;
            }

            if (t < 0)
            {
                t = t + n;
            }

            return t;
        }
    }
}
