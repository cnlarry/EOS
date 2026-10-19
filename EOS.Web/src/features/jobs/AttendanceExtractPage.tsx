import { AttendanceGenerateForm } from './AttendanceGenerateForm'

/** 考勤真实抽取生成（模块 180659）。 */
export function AttendanceExtractPage() {
  return (
    <AttendanceGenerateForm
      mode="extract"
      title="考勤真实抽取生成"
      description="按日期范围生成考勤记录，并按已批核排班的班次时间填充上下班（ON1/OUT1）。打卡数据接入尚未实现，当前生成规则与模拟生成相同。"
    />
  )
}
