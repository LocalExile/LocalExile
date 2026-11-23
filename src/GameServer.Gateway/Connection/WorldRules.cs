/// <summary>Current rules eligibility, checked at the existing send gate before synchronous projection.</summary>
public interface IWorldAdmissionRules
{
    bool WorldAdmissionActive { get; }
}
