using System;

namespace DockerDiagram.Models
{
    public sealed class SwarmServiceVersionConflictException : InvalidOperationException
    {
        public SwarmServiceVersionConflictException(string serviceId, ulong expectedVersion, ulong actualVersion)
            : base(
                $"Swarm service '{serviceId}'이(가) 다른 작업에서 변경되었습니다. " +
                $"요청 version={expectedVersion}, 현재 version={actualVersion}. 새로고침 후 다시 시도해 주세요.")
        {
            ServiceId = serviceId;
            ExpectedVersion = expectedVersion;
            ActualVersion = actualVersion;
        }

        public string ServiceId { get; }
        public ulong ExpectedVersion { get; }
        public ulong ActualVersion { get; }
    }

    public sealed class SwarmServiceMutationException : InvalidOperationException
    {
        public SwarmServiceMutationException(
            string operation,
            int? statusCode,
            string message,
            Exception innerException)
            : base(message, innerException)
        {
            Operation = operation;
            StatusCode = statusCode;
        }

        public string Operation { get; }
        public int? StatusCode { get; }
    }
}
