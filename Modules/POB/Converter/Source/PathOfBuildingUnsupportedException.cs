// Adapted from LocalExile feature/pob-import, 5394de0a. See NOTICE.md.
/// <summary>A recognized build feature that may be omitted without storing an invalid native record.</summary>
sealed class PathOfBuildingUnsupportedException(string message) : ArgumentException(message);
