/// <summary>Current rules eligibility, checked at the existing send gate before synchronous projection.</summary>
interface IWorldAdmissionRules
{
    bool WorldAdmissionActive { get; }
}
