import React, { useCallback, useEffect, useMemo } from 'react';
import { useDispatch, useSelector } from 'react-redux';
import AppState from 'App/State/AppState';
import FieldSet from 'Components/FieldSet';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import useModalOpenState from 'Helpers/Hooks/useModalOpenState';
import { kinds } from 'Helpers/Props';
import { cancelCommand, fetchCommands } from 'Store/Actions/commandActions';
import translate from 'Utilities/String/translate';
import QueuedTaskRow from './QueuedTaskRow';

const columns = [
  {
    name: 'trigger',
    label: '',
    isVisible: true,
  },
  {
    name: 'commandName',
    label: () => translate('Name'),
    isVisible: true,
  },
  {
    name: 'queued',
    label: () => translate('Queued'),
    isVisible: true,
  },
  {
    name: 'started',
    label: () => translate('Started'),
    isVisible: true,
  },
  {
    name: 'ended',
    label: () => translate('Ended'),
    isVisible: true,
  },
  {
    name: 'duration',
    label: () => translate('Duration'),
    isVisible: true,
  },
  {
    name: 'actions',
    isVisible: true,
  },
];

export default function QueuedTasks() {
  const dispatch = useDispatch();
  const { isFetching, isPopulated, items } = useSelector(
    (state: AppState) => state.commands
  );

  const [isCancelAllModalOpen, openCancelAllModal, closeCancelAllModal] =
    useModalOpenState(false);

  const queuedItems = useMemo(() => {
    return items.filter((item) => item.status === 'queued');
  }, [items]);

  const handleCancelAllPress = useCallback(() => {
    if (queuedItems.length === 0) {
      return;
    }

    openCancelAllModal();
  }, [openCancelAllModal, queuedItems.length]);

  const handleCancelAllConfirm = useCallback(() => {
    closeCancelAllModal();

    if (queuedItems.length === 0) {
      return;
    }

    queuedItems.forEach((item) => {
      dispatch(cancelCommand({ id: item.id }));
    });
  }, [closeCancelAllModal, dispatch, queuedItems]);

  useEffect(() => {
    dispatch(fetchCommands());
  }, [dispatch]);

  return (
    <FieldSet legend={translate('Queue')}>
      {isFetching && !isPopulated && <LoadingIndicator />}

      {isPopulated && (
        <>
          <div>
            <Button
              kind={kinds.DANGER}
              isDisabled={queuedItems.length === 0}
              onPress={handleCancelAllPress}
            >
              {translate('CancelAll')}
            </Button>
          </div>

          <Table columns={columns}>
            <TableBody>
              {items.map((item) => {
                return <QueuedTaskRow key={item.id} {...item} />;
              })}
            </TableBody>
          </Table>
        </>
      )}

      <ConfirmModal
        isOpen={isCancelAllModalOpen}
        kind={kinds.DANGER}
        title={translate('CancelAll')}
        message={translate('CancelAllQueuedTasksMessage')}
        confirmLabel={translate('YesCancel')}
        cancelLabel={translate('NoLeaveIt')}
        onConfirm={handleCancelAllConfirm}
        onCancel={closeCancelAllModal}
      />
    </FieldSet>
  );
}
