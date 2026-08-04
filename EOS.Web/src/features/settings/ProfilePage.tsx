export function ProfilePage() {
  return (
    <div className="row row-cards">
      <div className="col-lg-8">
        <section className="card">
          <div className="card-header"><h2 className="card-title">个人资料</h2></div>
          <div className="card-body">
            <div className="row g-3">
              <div className="col-md-6"><label className="form-label">姓名</label><input className="form-control" defaultValue="Demo User" /></div>
              <div className="col-md-6"><label className="form-label">角色</label><input className="form-control" defaultValue="系统管理员" disabled /></div>
              <div className="col-md-6"><label className="form-label">所属组织</label><input className="form-control" defaultValue="华东运营中心" /></div>
              <div className="col-md-6"><label className="form-label">语言</label><select className="form-select" defaultValue="zh-CN"><option value="zh-CN">简体中文</option></select></div>
            </div>
          </div>
          <div className="card-footer text-end"><button className="btn btn-primary" type="button">保存设置</button></div>
        </section>
      </div>
    </div>
  )
}
