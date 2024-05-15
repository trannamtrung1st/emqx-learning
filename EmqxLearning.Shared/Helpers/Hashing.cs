namespace EmqxLearning.Shared.Helpers;

public static class Hashing
{
    public static byte[] Md5Hash(byte[] input)
    {
        byte[] hash;
        using (var md5 = System.Security.Cryptography.MD5.Create())
        {
            md5.TransformFinalBlock(input, 0, input.Length);
            hash = md5.Hash;
        }
        return hash;
    }
}