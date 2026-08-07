namespace RemoteFlow.Application.Abstractions;

public interface ISecureRandom
{
    byte[] GetBytes(int count);
}
