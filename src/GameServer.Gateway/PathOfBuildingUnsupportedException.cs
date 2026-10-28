/// <summary>A recognized build feature that may be omitted without storing an invalid native record.</summary>
sealed class PathOfBuildingUnsupportedException(string message) : ArgumentException(message);
