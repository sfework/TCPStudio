import React from 'react'
import { withRouter, host } from '../Common/Common';
import { ContentPage } from '../Common/Component';
import ConnectionList from './Connection/ConnectionList';
import Workbench from './Connection/Workbench';

class Home extends React.Component {
  constructor(props) {
    super(props);
    this.state = { selectedId: null, loading: false, list: [] };
    this.connRef = null;
    this.workRef = null;
  }
  componentDidMount() {
    // 注册宿主 action 分发：状态变化刷新连接列表，数据转给工作台
    host.bind('connection.status', () => this.connRef?.reload());
    host.bind('data.received', (data) => this.workRef?.onEvent(data));
  }
  componentWillUnmount() {
    host.unBind('data.received', 'connection.status');
  }
  onSelect = (id) => {
    this.setState({ selectedId: id });
  }
  onListChange = (list) => {
    this.setState({ list });
  }
  render() {
    const conn = this.state.list.find(x => x.id === this.state.selectedId) ?? null;
    return <ContentPage loading={this.state.loading}>
      <div className="mainLayout">
        <div className="mainLayoutLeft">
          <ConnectionList ref={(el) => this.connRef = el} selectedId={this.state.selectedId}
            onSelect={this.onSelect} onChange={this.onListChange} />
        </div>
        <div className="mainLayoutRight">
          <Workbench ref={(el) => this.workRef = el} connection={conn} connections={this.state.list}
            onConnectionsChanged={() => this.connRef?.reload()} />
        </div>
      </div>
    </ContentPage>
  }
}
export default withRouter(Home)
