using DryIoc;
using DryIoc.MefAttributedModel;
using Mozart.Collections;
using Mozart.Extensions;
using Mozart.SeePlan.DataModel;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml;
using static LinqToDB.Reflection.Methods.LinqToDB.Insert;

namespace FabSimulator
{
    // criteria 패턴의 카테고리로 나누는 enum
    public enum CriteriaType { doubleEqualMixed, stringEqualMixed, doubleCommaMixed, stringCommaMixed, onlyNumeric, numCharCombo, onlyChar }

    // Criteria를 필요한 형태로 Converting 하는 과정을 1회만 수행하려고 상속하여 구현
    internal class FabWeightFactor : WeightFactor
    {
        // criteria를 담을 List와 Dictinary
        public IList criteriaList;
        public IDictionary criteriaDict;

        public FabWeightFactor(string name, double weightFactor, float sequence, FactorType type, OrderType orderType, string criteria)
            : base(name, weightFactor, sequence, type, orderType)
        {
            Criteria = SplitCriteria(criteria);

            InitializeParameters();
        }

        public object[] SplitCriteria(string criteria)
        {
            if (string.IsNullOrEmpty(criteria))
                return null;

            // First 구분자로 항상 Semi-Colon을 사용.
            return criteria.Split(';');
        }

        private void InitializeParameters()
        {
            // 현재 QTIME_FACTOR와 LOT_PROGRESS_FACTOR가 같은 Criteria 값을 넣을 수 있기에 Classify에서 분리시킴
            // 추후 Factor의 Criteria 값을 정형화 후 바꿀 예정
            if (Name.Contains("QTIME_FACTOR"))
            {
                // QTime Factor는 기본값을 가지고 있는 팩터이기에 미입력된 Criteria가 들어올 수 있음
                // 이에 Criteira의 null check보다 위의 단계에서 진행

                criteriaList = new List<double>() { 0.9, 5.0, 0.7, 0.0 };

                if (Criteria != null)
                {
                    string[] splitValues = Convert.ToString(Criteria).Split(',');

                    for (int i = 0; i < splitValues.Length; i++)
                    {
                        if (double.TryParse(splitValues[i], out double result))
                            criteriaList[i] = result;
                    }
                }
                return;
            }

            if (Criteria.IsNullOrEmpty())
                return;

            // Criteria가 어떤 타입을 써야하는지 파악하자.
            var caseOfCriteria = ClassifyTypeOfCriteria(Convert.ToString(Criteria[0]));
            
            switch (caseOfCriteria)
            {
                // Criteria 중간에 =이 들어가고 앞이 숫자인 경우
                case (int)CriteriaType.doubleEqualMixed:
                    criteriaDict = new Dictionary<double, double>();
                    FillCriteriaDictionary<double, double>('=');
                    break;
                // Criteria 중간에 =이 들어가고 앞이 문자열인 경우
                case (int)CriteriaType.stringEqualMixed:
                    criteriaDict = new Dictionary<string, double>();
                    FillCriteriaDictionary<string, double>('=');
                    break;
                // Criteria 중간에 ,가 들어가고 앞이 숫자인 경우
                case (int)CriteriaType.doubleCommaMixed:
                    criteriaDict = new Dictionary<double, double>();
                    FillCriteriaDictionary<double, double>(',');
                    break;
                // Criteria 중간에 ,가 들어가고 앞이 문자열인 경우
                case (int)CriteriaType.stringCommaMixed:
                    criteriaDict = new Dictionary<string, double>();
                    FillCriteriaDictionary<string, double>(',');
                    break;
                // Criteria에 숫자만 들어있는 경우
                case (int) CriteriaType.onlyNumeric:
                    criteriaList = new List<double>(); 
                    foreach (var value in Criteria)
                        criteriaList.Add(Convert.ToDouble(value));
                    break;
                // Criteria가 숫자 + 문자 1개로 이루어져 있는 경우
                case (int) CriteriaType.numCharCombo:
                    criteriaList = new List<double>();
                    foreach (var value in Criteria)
                    {
                        var doubleHours = Helper.GetDurationHoursWithChar(Convert.ToString(value));
                        criteriaList.Add(doubleHours);
                    }
                    break;
                // Criteria가 문자열일 경우
                case (int)CriteriaType.onlyChar:
                    criteriaList = new List<string>();
                    foreach (var value in Criteria)
                        criteriaList.Add(value.ToString());
                    break;
            }
        }
        
        // 정규표현식을 사용해서 Criteria의 Type을 결정해 주자
        private int ClassifyTypeOfCriteria(string criteria)
        {
            // 0번째 타입: '='을 중간에 두고 앞이 숫자인 형태
            // =이 아닌 문자들과 '=' 과 =이 아닌 문자들이 결합되어 있는 형태라면 0을 반환
            if (Regex.IsMatch(criteria, @"^[0-9]+=[^=]+$"))
            {
                return (int)CriteriaType.doubleEqualMixed;
            }
            // 1번째 타입: '='을 중간에 두고 앞이 문자열인 형태
            else if (Regex.IsMatch(criteria, @"^[A-z]+=[^=]+$"))
            {
                return (int)CriteriaType.stringEqualMixed;
            }
            // 2번째 타입 : ','을 중간에 두고 앞이 숫자인 형태
            if (Regex.IsMatch(criteria, @"^[0-9]+\,[^,]+$"))
            {
                return (int)CriteriaType.doubleCommaMixed;
            }
            // 3번째 타입 : ','을 중간에 두고 앞이 문자열인 형태
            if (Regex.IsMatch(criteria, @"^[A-z]+\,[^,]+$"))
            {
                return (int)CriteriaType.stringCommaMixed;
            }
            // 4번째 타입: 숫자로만 구성된 형태
            else if (double.TryParse(criteria, out _))
            {
                return (int)CriteriaType.onlyNumeric;
            }
            // 5번째 타입: 숫자 + 문자의 혼합인 형태
            else if (Regex.IsMatch(criteria, @"[0-9]+[A-z]{1}"))
            {
                return (int)CriteriaType.numCharCombo;
            }

            // 6번째 타입: 문자열의 형태
            return (int)CriteriaType.onlyChar;
        }

        private void FillCriteriaDictionary<TKey, TValue>(char delimeter)
        {
            foreach (string value in Criteria)
            {
                if (value != string.Empty)
                {
                    // value를 delimeter를 기준으로 나누자.
                    var splitedCriteria = value.Split(delimeter);

                    TKey tkey = (TKey)Convert.ChangeType(splitedCriteria[0], typeof(TKey));
                    TValue tvalue = (TValue)Convert.ChangeType(splitedCriteria[1], typeof(TValue));

                    criteriaDict.Add(tkey, tvalue);
                }
            }
        }
    }
}
