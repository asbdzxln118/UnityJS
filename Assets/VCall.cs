﻿using UnityEngine;
using UnityEditor;
using System;
using System.Collections;
using System.Text;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using System.Collections;
using System.IO;
using System.Text.RegularExpressions;



public class VCall
{
    public class JSParam
    {
        public int index; // 参数位置
        public object csObj; //对应的cs对象，对于基础类型，string，枚举，这个为null
        public bool isWrap { get { return csObj != null && csObj is ValueTypeWrap2.ValueTypeWrap; } }
        public object wrappedObj { get { return ((ValueTypeWrap2.ValueTypeWrap)csObj).obj; } set { ((ValueTypeWrap2.ValueTypeWrap)csObj).obj = value; } }
        public bool isArray;
        public bool isNull;
    }
    // CS函数的参数信息
    // 只存储一些要使用多次的
    public class CSParam
    {
        public bool isRef;
        public bool isOptional;
        public bool isArray;
    }

    public List<JSParam> lstJSParam;
    public int jsParamCount { get { return lstCSParam.Count; } }
    public List<CSParam> lstCSParam;
    public int csParamCount { get { return lstCSParam.Count; } }
    public MethodInfo m_Method;
    public ParameterInfo[] m_ParamInfo;
    public object[] callParams;

    IntPtr cx;
    IntPtr vp;

    public void Reset(IntPtr cx, IntPtr vp)
    {
        if (lstJSParam == null)
            lstJSParam = new List<JSParam>();
        else
            lstJSParam.Clear();
        if (lstCSParam == null)
            lstCSParam = new List<CSParam>();
        else
            lstCSParam.Clear();
        m_Method = null;
        m_ParamInfo = null;
        callParams = null;

        this.cx = cx;
        this.vp = vp;
    }

    /*
     * ExtractCSParams
     *
     * extract some info to use latter
     * write into m_ParamInfo and lstCSParam
     */
    public void ExtractCSParams()
    {
        if (m_ParamInfo == null)
            m_ParamInfo = m_Method.GetParameters();
        for (int i = 0; i < m_ParamInfo.Length; i++)
        {
            ParameterInfo p = m_ParamInfo[i];
            CSParam csParam = new CSParam();
            csParam.isOptional = p.IsOptional;
            csParam.isRef = p.ParameterType.IsByRef;
            csParam.isArray = p.ParameterType.IsArray;
            lstCSParam.Add(csParam);
        }
    }

    /*
     * ExtractJSParams
     * 
     * write into lstJSParam
     * 
     * RETURN
     * false -- fail
     * true  -- success
     * 
     * 对于枚举类型、基本类型：没有处理
     */
    public bool ExtractJSParams(int start, int count)
    {
        for (int i = 0; i < count; i++)
        {
            int index = i + start;
            bool bUndefined = SMDll.JShelp_ArgvIsUndefined(cx, vp, index);
            if (bUndefined)
                return true;

            JSParam jsParam = new JSParam();
            jsParam.index = index;
            jsParam.isNull = SMDll.JShelp_ArgvIsNull(cx, vp, index);
            jsParam.isArray = false;
            jsParam.csObj = null;

            IntPtr jsObj = SMDll.JShelp_ArgvObject(cx, vp, index);
            if (jsObj == IntPtr.Zero)
            {
                jsParam.csObj = null;
            }
            else if (SMDll.JS_IsArrayObject(cx, jsObj))
            {
                jsParam.isArray = true;
            }
            else
            {
                object csObj = SMData.getNativeObj(jsObj);
                if (csObj == null)
                {
                    Debug.Log("ExtractJSParams: CSObject is not found");
                    return false;
                }
                jsParam.csObj = csObj;
            }
            lstJSParam.Add(jsParam);
        }
        return true;
    }

    /*
     * MatchOverloadedMethod
     * 
     * write into this.method and this.ps
     *
     */
    public int MatchOverloadedMethod(MethodInfo[] methods, int methodIndex)
    {
        for (int i = methodIndex; i < methods.Length; i++)
        {
            MethodInfo method = methods[i];
            if (method.Name != methods[methodIndex].Name)
                return -1;
            ParameterInfo[] ps = method.GetParameters();
            if (jsParamCount > ps.Length)
                continue;
            for (int j = 0; j < ps.Length; j++)
            {
                ParameterInfo p = ps[j];
                if (j < jsParamCount)
                {
                    if (p.ParameterType.IsArray)
                    {
                        // todo
                        // 重载函数只匹配是否数组
                        // 无法识别2个都是数组参数但是类型不同的重载函数，这种情况只会调用第1个
                        if (!lstJSParam[j].isArray)
                            continue;
                    }
                    else if (!lstJSParam[j].isWrap)
                    {
                        if (lstJSParam[j].csObj == null || p.ParameterType != lstJSParam[j].csObj.GetType())
                            continue;
                    }
                    else if (lstJSParam[j].isWrap)
                    {
                        if (p.ParameterType != lstJSParam[j].wrappedObj.GetType())
                            continue;
                    }
                    else
                        continue;
                }
                else
                {
                    if (!p.IsOptional)
                        continue;
                }
            }
            // yes, this method is what we want
            this.m_Method = method;
            this.m_ParamInfo = ps;
            return i;
        }
        return -1;
    }

    // index means 
    // lstJSParam[index]
    // lstCSParam[index]
    // ps[index]
    // for calling property/field
    public object JSValue_2_CSObject(Type t, int paramIndex)
    {
        if (t.IsArray)
        {
            Debug.LogError("JSValue_2_CSObject: could not pass an array");
            return null;
        }

        if (t.IsByRef)
            t = t.GetElementType();

        if (t == typeof(string))
            return SMDll.JShelp_ArgvString(cx, vp, paramIndex);
        else if (t.IsEnum)
            return SMDll.JShelp_ArgvInt(cx, vp, paramIndex);
        else if (t.IsPrimitive)
        {
            if (t == typeof(System.Boolean))
            {
                return SMDll.JShelp_ArgvBool(cx, vp, paramIndex);
            }
            else if (t == typeof(System.Char) ||
                t == typeof(System.Byte) || t == typeof(System.SByte) ||
                t == typeof(System.UInt16) || t == typeof(System.Int16) ||
                t == typeof(System.UInt32) || t == typeof(System.Int32) ||
                t == typeof(System.UInt64) || t == typeof(System.Int64))
            {
                return SMDll.JShelp_ArgvInt(cx, vp, paramIndex);
            }
            else if (t == typeof(System.Single) || t == typeof(System.Double))
            {
                return SMDll.JShelp_ArgvDouble(cx, vp, paramIndex);
            }
            else
            {
                Debug.Log("ConvertJSValue2CSValue: Unknown primitive type: " + t.ToString());
            }
        }
        //         else if (t.IsValueType)
        //         {
        // 
        //         }
        else if (typeof(UnityEngine.Object).IsAssignableFrom(t))
        {
            if (SMDll.JShelp_ArgvIsNull(cx, vp, paramIndex))
                return null;

            IntPtr jsObj = SMDll.JShelp_ArgvObject(cx, vp, paramIndex);
            if (jsObj == IntPtr.Zero)
                return null;

            object csObject = SMData.getNativeObj(jsObj);
            return csObject;
        }
        else
        {
            Debug.Log("ConvertJSValue2CSValue: Unknown CS type: " + t.ToString());
        }
        return null;
    }

    // index means 
    // lstJSParam[index]
    // lstCSParam[index]
    // ps[index]
    // for calling method
    public object JSValue_2_CSObject(int index)
    {
        JSParam jsParam = lstJSParam[index];
        int paramIndex = jsParam.index;
        CSParam csParam = lstCSParam[index];
        ParameterInfo p = m_ParamInfo[index];

        Type t = p.ParameterType;

        if (csParam.isRef)
            t = t.GetElementType();

        if (typeof(UnityEngine.Object).IsAssignableFrom(t))
        {
            if (jsParam.isNull)
                return null;

            if (jsParam.isWrap)
                return jsParam.wrappedObj;

            return jsParam.csObj;
        }

        return JSValue_2_CSObject(p.ParameterType, paramIndex);
    }

    /*
     * BuildMethodArgs
     * 
     * RETURN
     * null -- fail
     * not null -- success
     */
    public object[] BuildMethodArgs()
    {
        ArrayList args = new ArrayList();
        for (int i = 0; i < this.m_ParamInfo.Length; i++)
        {
            if (i < this.lstJSParam.Count)
            {
                JSParam jsParam = lstJSParam[i];
                if (jsParam.isWrap)
                {
                    args.Add(jsParam.wrappedObj);
                }
                else if (jsParam.isArray)
                {
                    // todo
                    // 
                }
                else if (jsParam.isNull)
                {
                    args.Add(null);
                }
                else
                {
                    args.Add(JSValue_2_CSObject(i));
                }
            }
            else
            {
                ParameterInfo p = this.m_ParamInfo[i];
                if (p.IsOptional)
                    args.Add(p.DefaultValue);
                else
                {
                    Debug.LogError("Not enough arguments calling function '" + m_Method.Name + "'");
                    return null;
                }
            }
        }

        return args.ToArray();
    }

    // CS -> JS
    // 将 cs 对象转换为 js 对象
    public SMDll.jsval CSObject_2_JSValue(object csObj)
    {
        SMDll.jsval val = new SMDll.jsval();
        SMDll.JShelp_SetJsvalUndefined(ref val);

        if (csObj == null)
        {
            return val;
        }

        Type t = csObj.GetType();
        if (t == typeof(void))
        {
            return val;
        }
        else if (t == typeof(string))
        {
            SMDll.JShelp_SetJsvalString(cx, ref val, (string)csObj);
        }
        else if (t.IsEnum)
        {
            SMDll.JShelp_SetJsvalInt(ref val, (int)csObj);
        }
        else if (t.IsPrimitive)
        {
            if (t == typeof(System.Boolean))
            {
                SMDll.JShelp_SetJsvalBool(ref val, (bool)csObj);
            }
            else if (t == typeof(System.Char) ||
                t == typeof(System.Byte) || t == typeof(System.SByte) ||
                t == typeof(System.UInt16) || t == typeof(System.Int16) ||
                t == typeof(System.UInt32) || t == typeof(System.Int32) ||
                t == typeof(System.UInt64) || t == typeof(System.Int64))
            {
                SMDll.JShelp_SetJsvalInt(ref val, (int)csObj);
            }
            else if (t == typeof(System.Single) || t == typeof(System.Double))
            {
                SMDll.JShelp_SetJsvalDouble(ref val, (double)csObj);
            }
            else
            {
                Debug.Log("CS -> JS: Unknown primitive type: " + t.ToString());
            }
        }
        //         else if (t.IsValueType)
        //         {
        // 
        //         }
        else if (t.IsArray)
        {
            // todo
            // 如果返回数组的数组可能会有问题
            Array arr = csObj as Array;
            if (arr.Length > 0 && arr.GetValue(0).GetType().IsArray)
            {
                Debug.LogWarning("cs return [][] may cause problems.");
            }

            IntPtr jsArr = SMDll.JS_NewArrayObject(cx, arr.Length);
            
            for (int i = 0; i < arr.Length; i++)
            {
                SMDll.jsval subVal = CSObject_2_JSValue(arr.GetValue(i));
                SMDll.JS_SetElement(cx, jsArr, (uint)i, ref subVal);
            }
            SMDll.JShelp_SetJsvalObject(ref val, jsArr);
        }
        else if (typeof(UnityEngine.Object).IsAssignableFrom(t))
        {
            IntPtr jsObj = SMData.getJSObj(csObj);
            if (jsObj == IntPtr.Zero)
            {
                jsObj = SMDll.JShelp_NewObjectAsClass(cx, CallJS.glob, t.Name);
                if (jsObj != null)
                    SMData.addNativeJSRelation(jsObj, csObj);
            }
            if (jsObj == IntPtr.Zero)
                SMDll.JShelp_SetJsvalUndefined(ref val);
            else
                SMDll.JShelp_SetJsvalObject(ref val, jsObj);
        }
        else
        {
            Debug.Log("CS -> JS: Unknown CS type: " + t.ToString());
            SMDll.JShelp_SetJsvalUndefined(ref val);
        }
        return val;
    }

    public void PushResult(object csObj)
    {
        // handle ref/out parameters
        for (int i = 0; i < lstCSParam.Count; i++)
        {
            if (lstCSParam[i].isRef)
            {
                lstJSParam[i].wrappedObj = callParams[i];
            }
        }

        SMDll.jsval val = CSObject_2_JSValue(csObj);
        SMDll.JShelp_SetRvalJSVAL(cx, vp, ref val);
    }


    public enum Oper
    {
        GET_FIELD = 0,
        SET_FIELD = 1,
        GET_PROPERTY = 2,
        SET_PROPERTY = 3,
        METHOD,
    }

    public int Call(IntPtr cx, uint argc, IntPtr vp)
    {
        this.Reset(cx, vp);

        // 前面4个参数是固定的
        Oper op = (Oper)SMDll.JShelp_ArgvInt(cx, vp, 0);
        int slot = SMDll.JShelp_ArgvInt(cx, vp, 1);
        int index = SMDll.JShelp_ArgvInt(cx, vp, 2);
        bool isStatic = SMDll.JShelp_ArgvBool(cx, vp, 3);

        if (slot < 0 || slot >= JSMgr.allTypeInfo.Count)
        {
            Debug.LogError("Bad slot: " + slot);
            return SMDll.JS_FALSE;
        }
        JSMgr.ATypeInfo aInfo = JSMgr.allTypeInfo[slot];

        int paramCount = 4;
        object csObj = null;
        if (!isStatic)
        {
            IntPtr jsObj = SMDll.JShelp_ArgvObject(cx, vp, 4);
            if (jsObj == IntPtr.Zero)
                return SMDll.JS_FALSE;

            csObj = SMData.getNativeObj(jsObj);
            if (csObj == null)
                return SMDll.JS_FALSE;

            paramCount++;
        }

        object result = null;

        switch (op)
        {
            case Oper.GET_FIELD:
                {
                    result = aInfo.fields[index].GetValue(csObj);
                }
                break;
            case Oper.SET_FIELD:
                {
                    FieldInfo field = aInfo.fields[index];
                    field.SetValue(csObj, JSValue_2_CSObject(field.FieldType, 4));
                }
                break;
            case Oper.GET_PROPERTY:
                {
                    result = aInfo.properties[index].GetValue(csObj, null);
                }
                break;
            case Oper.SET_PROPERTY:
                {
                    PropertyInfo property = aInfo.properties[index];
                    property.SetValue(csObj, JSValue_2_CSObject(property.PropertyType, 4), null);
                }
                break;
            case Oper.METHOD:
                {
                    bool overloaded = SMDll.JShelp_ArgvBool(cx, vp, paramCount);
                    paramCount++;

                    if (!this.ExtractJSParams(paramCount, (int)argc - paramCount))
                        return SMDll.JS_FALSE;

                    if (overloaded)
                    {
                        if (-1 == MatchOverloadedMethod(aInfo.methods, index))
                            return SMDll.JS_FALSE;
                    }
                    else
                    {
                        m_Method = aInfo.methods[index];
                    }

                    this.ExtractCSParams();

                    object[] args = BuildMethodArgs();
                    if (null == args)
                        return SMDll.JS_FALSE;

                    result = this.m_Method.Invoke(csObj, args);
                }
                break;
        }

        this.PushResult(result);
        return SMDll.JS_TRUE;
    }
}