namespace TeachingToolWpf.Models;

/// <summary>등록된 패턴 템플릿의 판정 역할(정상 패턴 vs 불량 패턴)을 정의한다.</summary>
public enum PatternRegistrationRole
{
    /// <summary>검출 시 합격(OK · 정상)으로 판정하는 기준 정상 패턴.</summary>
    Good = 0,

    /// <summary>검출 시 불량(NG · 불량)으로 판정하는 결함/이종 불량 패턴.</summary>
    Defect = 1
}
