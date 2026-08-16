namespace ClanSystem.CoreData
{
    /// <summary>
    /// Result of a backend call. Backend operations never throw for expected failures such as
    /// "clan is full" - callers branch on <see cref="IsSuccess"/> and surface <see cref="Message"/>.
    /// </summary>
    public readonly struct SocialResult
    {
        public bool IsSuccess { get; }
        public SocialErrorCode Error { get; }
        public string Message { get; }

        private SocialResult(bool isSuccess, SocialErrorCode error, string message)
        {
            IsSuccess = isSuccess;
            Error = error;
            Message = message;
        }

        public static SocialResult Success()
        {
            return new SocialResult(true, SocialErrorCode.None, string.Empty);
        }

        public static SocialResult Failure(SocialErrorCode error, string message)
        {
            return new SocialResult(false, error, message);
        }
    }

    /// <summary>
    /// Result of a backend call that carries a payload on success.
    /// </summary>
    /// <typeparam name="T">Type of the returned payload.</typeparam>
    public readonly struct SocialResult<T>
    {
        public bool IsSuccess { get; }
        public T Value { get; }
        public SocialErrorCode Error { get; }
        public string Message { get; }

        private SocialResult(bool isSuccess, T value, SocialErrorCode error, string message)
        {
            IsSuccess = isSuccess;
            Value = value;
            Error = error;
            Message = message;
        }

        public static SocialResult<T> Success(T value)
        {
            return new SocialResult<T>(true, value, SocialErrorCode.None, string.Empty);
        }

        public static SocialResult<T> Failure(SocialErrorCode error, string message)
        {
            return new SocialResult<T>(false, default, error, message);
        }
    }
}
